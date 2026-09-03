using System;
using System.Collections.Generic;
using System.Linq;
using BigAmbitions.Items;
using Entities;

namespace BigAmbitionsMonitor
{
    /// <summary>
    /// Запись расписания в игру. Повторяет то, что делает игровой ScheduleAutoFiller.ApplyResult:
    /// убрать старые смены → AddWorkShift → у сотрудников UpdateWeeklyHoursAndDays/UpdateAssignedWorkStationItems.
    /// Заменяет смены ТОЛЬКО перечисленных сотрудников (employeeIds) — так можно применять
    /// расписание по одной должности, не затирая остальных.
    /// ВСЕ методы Apply* должны вызываться в главном потоке (через MainThreadDispatcher).
    /// </summary>
    public static class ScheduleWriter
    {
        public class ShiftDto { public int dayIndex; public string employeeId; public int from; public int to; public string type; } // type: "Cleaning" — уборочный визит (место = станция уборки)
        public class SlotDto { public int from; public int to; }
        public class OpeningDayDto { public int dayIndex; public bool isOpen; public List<SlotDto> slots; }
        public class WindowDto { public int from; public int to; }

        public class ApplyRequest
        {
            public string street;
            public int number;
            public List<string> employeeIds; // чьи смены заменяем (даже если новых нет — очистим)
            public List<ShiftDto> shifts;
            public List<OpeningDayDto> openingHours; // необязательно: готовые часы работы по дням
            public WindowDto openingWindow;          // необязательно: желаемое окно — часы посчитаем по факту смен
        }

        public class ApplyResult { public bool ok; public int added; public int removed; public int hoursChanged; public int openHours; public string error; public List<string> warnings = new List<string>(); }

        public static ApplyResult Apply(ApplyRequest req)
        {
            var res = new ApplyResult();
            var g = SaveGameManager.Current;
            if (g == null) { res.error = "сейв не загружен"; return res; }
            if (req == null) { res.error = "пустой запрос"; return res; }

            var b = (g.BuildingRegistrations ?? new List<BuildingRegistration>())
                .FirstOrDefault(x => x != null && x.StreetName == req.street && x.StreetNumber == req.number);
            if (b == null) { res.error = "здание не найдено"; return res; }
            if (b.scheduleDays == null) { res.error = "у здания нет расписания"; return res; }

            var targetIds = new HashSet<string>((req.employeeIds ?? new List<string>()).Where(x => !string.IsNullOrEmpty(x)));
            var emps = (g.EmployeeInstances ?? new List<EmployeeInstance>())
                .Where(e => e != null && !string.IsNullOrEmpty(e.id) && targetIds.Contains(e.id)).ToList();
            if (emps.Count == 0) { res.error = "сотрудники не найдены"; return res; }
            var byId = emps.ToDictionary(e => e.id);

            List<ItemInstance> stations;
            try { stations = b.GetAssignableItems() ?? new List<ItemInstance>(); }
            catch (Exception e) { res.error = "GetAssignableItems: " + e.Message; return res; }
            if (stations.Count == 0) { res.error = "в здании нет рабочих мест"; return res; }

            // 1) убрать существующие смены целевых сотрудников (все дни)
            foreach (var d in b.scheduleDays)
            {
                if (d == null) continue;
                int before = d.workShifts?.Count ?? 0;
                d.RemoveAllWorkShiftsThatMatchPredicate(x => x != null && !string.IsNullOrEmpty(x.employeeId) && targetIds.Contains(x.employeeId));
                res.removed += before - (d.workShifts?.Count ?? 0);
            }

            // 2) добавить новые.
            // Порядок важен: назначаем места жадно по времени начала (раскраска интервального графа).
            // При порядке «по сотрудникам» смена могла не получить места, хотя одновременных смен
            // не больше числа касс — и тогда она молча отбрасывалась.
            foreach (var s in (req.shifts ?? new List<ShiftDto>())
                        .Where(x => x != null)
                        .OrderBy(x => x.dayIndex).ThenBy(x => x.from).ThenBy(x => x.to))
            {
                if (s == null || string.IsNullOrEmpty(s.employeeId) || s.to <= s.from) continue;
                if (!byId.TryGetValue(s.employeeId, out var emp)) continue;
                var day = b.scheduleDays.FirstOrDefault(d => d != null && (int)d.day == s.dayIndex);
                if (day == null) { res.warnings.Add($"день {s.dayIndex} не найден"); continue; }

                bool wantCleaning = string.Equals(s.type, "Cleaning", StringComparison.OrdinalIgnoreCase);
                var ws = wantCleaning
                    ? PickCleaningStation(stations, day, s.from, s.to, out bool allBusy)   // продавец подменяет уборщика
                    : PickWorkstation(stations, emp, day, s.from, s.to, out allBusy);
                if (ws == null)
                {
                    // Не сажаем кассира на станцию уборки и наоборот: лучше пропустить смену и сказать об этом.
                    res.warnings.Add(allBusy
                        ? $"{emp.characterData?.name}: {DayName(day)} {s.from}–{s.to} — {(wantCleaning ? "станция уборки занята" : "все места для его должности заняты")}, смена пропущена"
                        : (wantCleaning ? "нет станции уборки" : $"{emp.characterData?.name}: нет рабочего места под его должность"));
                    continue;
                }

                day.AddWorkShift(new WorkShift
                {
                    employeeId = emp.id,
                    itemInstanceId = ws.id,
                    startingHour = Math.Max(0, s.from),
                    endingHour = Math.Min(24, s.to),
                    type = IsCleaning(ws) ? WorkShiftType.Cleaning : WorkShiftType.Default,
                });
                res.added++;
            }

            // 3) пересчитать производные поля сотрудников (как ApplyResult игры)
            foreach (var e in emps)
            {
                try { e.UpdateWeeklyHoursAndDays(); } catch (Exception ex) { res.warnings.Add("UpdateWeeklyHoursAndDays: " + ex.Message); }
                try { e.UpdateAssignedWorkStationItems(); } catch (Exception ex) { res.warnings.Add("UpdateAssignedWorkStationItems: " + ex.Message); }
            }

            // 4) часы работы бизнеса. В игре это просто данные ScheduleDay (isOpen + openingHourSlots),
            // игровой BizMan тоже правит их напрямую.
            // openingWindow: считаем часы по ФАКТИЧЕСКИ записанным сменам (не по плану панели) —
            // иначе отброшенная при записи смена оставила бы магазин открытым без персонала.
            if (req.openingWindow != null)
            {
                int wFrom = Math.Max(0, Math.Min(23, req.openingWindow.from));
                int wTo = Math.Max(wFrom + 1, Math.Min(24, req.openingWindow.to));
                foreach (var day in b.scheduleDays)
                {
                    if (day == null) continue;
                    var staffed = new bool[24];
                    foreach (var sh in day.workShifts ?? new List<WorkShift>())
                    {
                        if (sh == null || sh.type == WorkShiftType.Cleaning) continue;
                        for (int h = Math.Max(wFrom, sh.startingHour); h < Math.Min(wTo, sh.endingHour); h++) staffed[h] = true;
                    }
                    var slots = new List<SlotDto>();
                    int start = -1;
                    for (int h = wFrom; h <= wTo; h++)
                    {
                        bool on = h < wTo && staffed[h];
                        if (on && start < 0) start = h;
                        if (!on && start >= 0) { slots.Add(new SlotDto { from = start, to = h }); start = -1; }
                    }
                    if (ApplyOpeningHours(day, slots, slots.Count > 0)) res.hoursChanged++;
                    res.openHours += slots.Sum(x => x.to - x.from);
                }
            }

            // Готовые слоты — путь для старой панели; при заданном окне они не нужны.
            foreach (var oh in (req.openingWindow == null ? req.openingHours : null) ?? new List<OpeningDayDto>())
            {
                if (oh == null) continue;
                var day = b.scheduleDays.FirstOrDefault(d => d != null && (int)d.day == oh.dayIndex);
                if (day == null) { res.warnings.Add($"день {oh.dayIndex} не найден"); continue; }
                if (ApplyOpeningHours(day, oh.slots, oh.isOpen)) res.hoursChanged++;
            }

            res.ok = true;
            return res;
        }

        private static bool IsCleaning(ItemInstance ws)
        {
            try { return ws?.ItemCached != null && ws.ItemCached.HasTag("ba:itemtag_iscleaningstation"); }
            catch { return false; }
        }

        // Записать часы работы дня. При закрытом дне слоты не трогаем: день без слотов игра
        // показывает как 8–16 по умолчанию (см. BizManPresentation.OvertakeBusiness).
        private static bool ApplyOpeningHours(ScheduleDay day, List<SlotDto> slots, bool isOpen)
        {
            var list = (slots ?? new List<SlotDto>())
                .Where(s => s != null && s.from >= 0 && s.to <= 24 && s.to > s.from)
                .OrderBy(s => s.from).ToList();
            bool changed = day.isOpen != isOpen;
            if (list.Count > 0)
            {
                var cur = (day.openingHourSlots ?? new List<OpeningHourSlot>()).OrderBy(s => s.startingHour).ToList();
                bool same = cur.Count == list.Count && cur.Zip(list, (c, s) => c.startingHour == s.from && c.endingHour == s.to).All(x => x);
                if (!same)
                {
                    day.openingHourSlots = list.Select(s => new OpeningHourSlot(s.from, s.to)).ToList();
                    changed = true;
                }
            }
            day.isOpen = isOpen;
            return changed;
        }

        private static string DayName(ScheduleDay d) { try { return d.day.ToString(); } catch { return "?"; } }

        // Свободная в эти часы станция уборки (для уборочных визитов продавцов).
        private static ItemInstance PickCleaningStation(List<ItemInstance> stations, ScheduleDay day, int from, int to, out bool allBusy)
        {
            allBusy = false;
            var cleaning = stations.Where(IsCleaning).ToList();
            if (cleaning.Count == 0) return null;
            foreach (var w in cleaning)
            {
                bool busy = (day.workShifts ?? new List<WorkShift>())
                    .Any(x => x != null && x.itemInstanceId == w.id && x.startingHour < to && from < x.endingHour);
                if (!busy) return w;
            }
            allBusy = true;
            return null;
        }

        private static bool SuitsSkill(ItemInstance ws, string skill)
        {
            var suit = ws?.ItemCached?.suitableSkills;
            return suit != null && !string.IsNullOrEmpty(skill) && Array.IndexOf(suit, skill) >= 0;
        }

        // Места под должность сотрудника: по основному навыку (GetPrimarySkill — как считает игра);
        // если таких нет — по любому его навыку. Места «без требований» подходят всем.
        private static List<ItemInstance> StationsFor(List<ItemInstance> stations, EmployeeInstance e)
        {
            string primary = null; try { primary = e.GetPrimarySkill(); } catch { }
            var list = stations.Where(w => w != null && (SuitsSkill(w, primary) || (w.ItemCached?.suitableSkills?.Length ?? 0) == 0)).ToList();
            if (list.Count > 0) return list;
            var skills = e.characterData?.skills;
            if (skills == null) return new List<ItemInstance>();
            return stations.Where(w => w != null && skills.Any(sk => sk != null && SuitsSkill(w, sk.name))).ToList();
        }

        // Подбор: место под должность → уже закреплённое за сотрудником → свободное в эти часы.
        // Если все места должности заняты в эти часы — null и allBusy=true (на чужое место не сажаем).
        private static ItemInstance PickWorkstation(List<ItemInstance> stations, EmployeeInstance e, ScheduleDay day, int from, int to, out bool allBusy)
        {
            allBusy = false;
            var suitable = StationsFor(stations, e);
            if (suitable.Count == 0) return null;
            var assigned = new HashSet<string>(e.assignedWorkStationItems ?? new List<string>());
            foreach (var w in suitable.OrderByDescending(w => assigned.Contains(w.id)))
            {
                bool busy = (day.workShifts ?? new List<WorkShift>())
                    .Any(x => x != null && x.itemInstanceId == w.id && x.startingHour < to && from < x.endingHour);
                if (!busy) return w;
            }
            allBusy = true;
            return null;
        }
    }
}
