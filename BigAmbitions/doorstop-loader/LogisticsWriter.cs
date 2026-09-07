using System;
using System.Collections.Generic;
using System.Linq;
using BigAmbitions.Items;
using Buildings.Office.Headquarters;
using Entities;

namespace BigAmbitionsMonitor
{
    /// <summary>
    /// Маршрут менеджера по логистике: пункты назначения склада и целевые остатки в магазинах.
    /// Как LogisticsManagerPlanUI: пункт = LogisticsManagerPlanDestination с deliveryTargetAddress,
    /// цель по товару = ItemAmountTarget{itemName, targetAmount}; ноль означает «не возить».
    /// Число пунктов ограничено MaxDestinations (машины склада + навык логиста).
    /// ГЛАВНЫЙ ПОТОК.
    /// </summary>
    public static class LogisticsWriter
    {
        public class TargetDto { public string item; public int amount; }
        public class DestinationDto { public string street; public int number; public List<TargetDto> targets; }
        public class PlanRequest { public string planId; public List<DestinationDto> destinations; }
        public class Result { public bool ok; public int destinations; public int targets; public string error; public List<string> warnings = new List<string>(); }

        public static Result Apply(PlanRequest req)
        {
            var res = new Result();
            var g = SaveGameManager.Current;
            if (g == null) { res.error = "сейв не загружен"; return res; }
            if (req == null || string.IsNullOrEmpty(req.planId)) { res.error = "план не выбран"; return res; }

            var plan = (g.logisticsManagerPlans ?? new List<LogisticsManagerPlan>())
                .FirstOrDefault(x => x != null && x.id == req.planId);
            if (plan == null) { res.error = "план не найден"; return res; }
            if (plan.targetAddress == null) { res.error = "у плана не выбран склад"; return res; }

            int max = 0;
            try { max = plan.MaxDestinations; } catch { }
            var list = (req.destinations ?? new List<DestinationDto>())
                .Where(d => d != null && !string.IsNullOrEmpty(d.street)).ToList();
            if (max > 0 && list.Count > max)
            {
                res.warnings.Add($"пунктов больше максимума ({max}) — лишние отброшены");
                list = list.Take(max).ToList();
            }

            var fresh = new List<LogisticsManagerPlanDestination>();
            foreach (var d in list)
            {
                var reg = (g.BuildingRegistrations ?? new List<BuildingRegistration>())
                    .FirstOrDefault(x => x != null && x.StreetName == d.street && x.StreetNumber == d.number);
                if (reg == null) { res.warnings.Add(d.street + " " + d.number + ": здание не найдено"); continue; }
                var dest = new LogisticsManagerPlanDestination
                {
                    deliveryTargetAddress = reg.Address,
                    stockTargets = new List<ItemAmountTarget>(),
                    isUiCollapsed = true,
                };
                foreach (var t in d.targets ?? new List<TargetDto>())
                {
                    if (t == null || string.IsNullOrEmpty(t.item)) continue;
                    int amount = Math.Max(0, Math.Min(9999999, t.amount));
                    if (amount == 0) continue;   // ноль игра хранит как отсутствие цели
                    dest.stockTargets.Add(new ItemAmountTarget(t.item) { targetAmount = amount });
                    res.targets++;
                }
                fresh.Add(dest);
            }

            plan.destinations = fresh;
            res.destinations = fresh.Count;
            try { SaveGameManager.MarkChange(); } catch { }
            res.ok = true;
            return res;
        }
    }
}
