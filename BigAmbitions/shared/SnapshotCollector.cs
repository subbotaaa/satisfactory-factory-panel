using System;
using System.Collections.Generic;
using System.Linq;
using BigAmbitions.Items;
using Entities;
using Newtonsoft.Json;

namespace BigAmbitionsMonitor
{
    /// <summary>
    /// Собирает JSON-снапшот из GameInstance: бизнесы, проблемы, остатки на складах, финансы.
    /// Общий код для мода (live: SaveGameManager.Current) и внешнего ридера сейвов.
    ///
    /// Проблемы (TodoTasks) в файле сохранения не хранятся — игра пересчитывает их на лету,
    /// поэтому мы выводим их сами из сохранённых полей (пустые полки, недовольные сотрудники,
    /// закрытый бизнес, неоплаченные налоги, отсутствие сотрудников).
    /// </summary>
    public static class SnapshotCollector
    {
        // Порог «мало товара» для полок магазина
        private const int LowStockThreshold = 10;

        // Сколько последних дней истории заказов учитывать для аналитики продаж.
        private const int SalesWindowDays = 7;

        /// <summary>
        /// Резолвер локализованного имени товара по ключу (например "ba:itemname_frozenfood").
        /// Хост (плагин в игре) выставляет его в LocalizorManager.GetLocalization; вне игры null.
        /// </summary>
        public static Func<string, string> LocalizeItem;

        /// <summary>Доступна ли запись в игру (главный поток подключён). Выставляет хост.</summary>
        public static Func<bool> CanWrite;

        private static readonly Dictionary<string, string> _nameCache = new Dictionary<string, string>();

        /// <summary>Локализованное имя товара с кэшем и запасным «причёсыванием» ключа.</summary>
        private static string ItemName(string key)
        {
            if (string.IsNullOrEmpty(key)) return "—";
            if (_nameCache.TryGetValue(key, out var cached)) return cached;

            string name = null;
            var resolver = LocalizeItem;
            if (resolver != null)
            {
                try
                {
                    var s = resolver(key);
                    // Игнорируем пустой результат и «неразрешённый» ключ (вернулся сам ключ).
                    if (!string.IsNullOrEmpty(s) && s != key && s.IndexOf(':') < 0)
                        name = s;
                }
                catch { }
            }
            name ??= Prettify(key);
            _nameCache[key] = name;
            return name;
        }

        private static string Prettify(string key)
        {
            var s = key;
            int i = s.IndexOf(':');
            if (i >= 0) s = s.Substring(i + 1);
            if (s.StartsWith("itemname_")) s = s.Substring("itemname_".Length);
            s = s.Replace('_', ' ');
            return s.Length > 0 ? char.ToUpper(s[0]) + s.Substring(1) : s;
        }

        public static string BuildSnapshotJson(GameInstance g)
        {
            if (g == null)
                return "{\"error\":\"no savegame loaded\"}";

            var employeesByAddr = IndexEmployees(g);
            var profitsByAddr = IndexDailyProfits(g);

            var businesses = new List<object>();
            var problems = new List<object>();

            if (g.BuildingRegistrations != null)
            {
                foreach (var b in g.BuildingRegistrations)
                {
                    if (b == null || !b.RentedByPlayer) continue;
                    try
                    {
                        var key = AddrKey(b.StreetName, b.StreetNumber);
                        var empList = employeesByAddr.TryGetValue(key, out var lst) ? lst : new List<EmployeeInstance>();
                        profitsByAddr.TryGetValue(key, out var profits);
                        businesses.Add(CollectBuilding(b, empList, profits));
                        DeriveBuildingProblems(b, empList.Count, problems);
                    }
                    catch (Exception) { /* одно здание не должно ломать снапшот */ }
                }
            }

            DeriveGlobalProblems(g, employeesByAddr, problems);

            var snapshot = new Dictionary<string, object>
            {
                ["updatedAt"] = DateTime.Now.ToString("o"),
                ["game"] = new
                {
                    day = g.Day,
                    hour = g.Hour,
                    minute = (int)g.Minute,
                    money = g.Money,
                    netWorth = g.NetWorth,
                    saveName = g.SaveGameName,
                    canWrite = SafeCanWrite(),
                },
                ["businesses"] = businesses,
                ["problems"] = problems,
                ["finances"] = CollectFinances(g),
                ["hr"] = CollectHr(g),
                ["logistics"] = CollectLogistics(g),
                ["wholesalers"] = CollectWholesalers(),
                ["delivery"] = SafeGet(() => DeliveryProvider?.Invoke(), (object)null),
                ["rentals"] = SafeGet(() => RentalsProvider?.Invoke(), (object)null),
                ["importers"] = SafeGet(() => ImportersProvider?.Invoke(), (object)null),
                ["boxSizes"] = SafeGet(() => BoxSizesProvider?.Invoke(), (object)null),
                ["services"] = SafeGet(() => ServicesProvider?.Invoke(), (object)null),
                ["autopilot"] = SafeGet(() => AutopilotProvider?.Invoke(), (object)null),
            };
            return JsonConvert.SerializeObject(snapshot);
        }

        // ВАЖНО: всё, что трогает Unity/native (ItemHelper, ItemCached, GetAssignableItems, Building,
        // JobDemand.Fulfilled), нельзя вызывать из фонового потока — игра падает. Хост считает это в
        // главном потоке и отдаёт через кэш-провайдеры ниже (вне игры они null → пустые данные).
        public static Func<string, List<object>> PricesFor;            // по ключу здания
        public static Func<string, List<object>> WorkstationsFor;      // по ключу здания
        public static Func<string, Dictionary<string, int>> CapacityFor; // по ключу здания: товар -> ёмкость
        public static Func<string, int> FreeCapacityFor;                // по ключу здания
        public static Func<string, float> WholesalePrice;               // по товару (из кэша)
        public static Func<string, int> WeeklyMaxFor;                   // недельный лимит заказа по товару (из кэша)
        public static Func<string, List<string>> UnfulfilledFor;        // по id сотрудника
        public static Func<object> WholesalersProvider;                 // список оптовиков (из кэша)
        public static Func<object> DeliveryProvider;                    // сроки доставки (из кэша)
        public static Func<string, object> WarehouseInfoFor;            // по ключу здания: хранилища и автопарк
        public static Func<object> RentalsProvider;                     // свободные здания в аренду (из кэша)
        public static Func<object> ImportersProvider;                   // импортёры и их каталог (из кэша)
        public static Func<string, string> StreetNameFor;               // «ba:street_fifthavenue» → «5th Avenue»
        public static Func<string, List<string>> AllowedSkillsFor;      // по ключу здания: какие навыки принимает
        public static Func<string, float> TrainingCostFor;              // по id сотрудника: цена обучения
        public static Func<string, int> MaxDestinationsFor;             // по id плана логиста: лимит пунктов
        public static Func<object> BoxSizesProvider;                    // товар → размер коробки (из кэша)
        public static Func<object> ServicesProvider;                    // услуги — продаются, но не доставляются
        public static Func<object> AutopilotProvider;                   // состояние автопилота (конфиг + журнал)

        private static float Safe(Func<float> f) { try { return f(); } catch { return 0f; } }
        private static T SafeGet<T>(Func<T> f, T def) { try { var v = f(); return v == null ? def : v; } catch { return def; } }

        // ---------- HR: удовлетворённость, невыполненные требования, кандидаты ----------
        private static List<string> Unfulfilled(EmployeeInstance e)
        {
            if (e == null || string.IsNullOrEmpty(e.id) || UnfulfilledFor == null) return new List<string>();
            return SafeGet(() => UnfulfilledFor(e.id), new List<string>());
        }

        private static object CollectHr(GameInstance g)
        {
            var staff = new List<object>();
            foreach (var e in g.EmployeeInstances ?? new List<EmployeeInstance>())
            {
                if (e == null) continue;
                try
                {
                    var skl = e.characterData?.skills;
                    var primary = (skl != null && skl.Count > 0) ? skl[0] : null;
                    // Обучение: сессия заканчивается на следующий день в 17:00 (EmployeeInstance.RunHourly)
                    object training = null;
                    if (e.trainingSession != null)
                        training = new
                        {
                            skill = ItemName(e.trainingSession.skill),
                            startDay = e.trainingSession.startDay,
                            endsDay = e.trainingSession.startDay + 1,
                            endsHour = 17,
                        };
                    staff.Add(new
                    {
                        id = e.id,
                        name = e.characterData?.name ?? "—",
                        street = e.assignedAddress?.streetName,
                        number = e.assignedAddress?.streetNumber ?? 0,
                        skill = SafeStr(() => ItemName(e.GetPrimarySkill())),
                        skillValue = primary == null ? 0 : (int)Math.Round(primary.value),
                        skills = skl == null ? new List<object>() : skl.Where(x => x != null)
                            .Select(x => (object)new { name = ItemName(x.name), value = (int)Math.Round(x.value) }).ToList(),
                        wage = e.hourlyWage,
                        weeklyHours = e.assignedWeeklyHours,
                        satisfaction = (int)Math.Round(e.satisfaction),
                        quitWarning = e.hasSendQuitWarning,
                        unfulfilled = Unfulfilled(e),
                        demands = (e.demands ?? new List<string>()).Select(ItemName).ToList(),
                        absent = e.isAbsent,
                        training,
                        trainingCost = SafeGet(() => TrainingCostFor == null ? 0f : TrainingCostFor(e.id), 0f),
                        canTrain = primary != null && e.trainingSession == null && !e.isAbsent && primary.value < 100f,
                    });
                }
                catch { }
            }
            var candidates = new List<object>();
            foreach (var c in g.CandidateEmployeeInstances ?? new List<EmployeeInstance>())
            {
                if (c == null) continue;
                try
                {
                    var skl = c.characterData?.skills;
                    candidates.Add(new
                    {
                        id = c.id,
                        name = c.characterData?.name ?? "—",
                        skill = SafeStr(() => ItemName(c.GetPrimarySkill())),
                        wage = c.hourlyWage,
                        skills = skl == null ? new List<object>() : skl.Where(s => s != null).Select(s => (object)new { name = ItemName(s.name), value = (int)Math.Round(s.value) }).ToList(),
                        demands = (c.demands ?? new List<string>()).Select(ItemName).ToList(),
                        skillKeys = skl == null ? new List<string>() : skl.Where(s2 => s2 != null).Select(s2 => s2.name).ToList(),
                        skillValue = (skl != null && skl.Count > 0) ? (int)Math.Round(skl[0].value) : 0,
                        fromJobBoard = c.candidateInfo?.fromJobBoard ?? false,
                        hoursLeft = c.candidateInfo?.hoursUntilExpiring ?? 0,
                    });
                }
                catch { }
            }
            return new { staff, candidates };
        }

        private static string SafeStr(Func<string> f) { try { return f(); } catch { return null; } }

        // ---------- Логистика: контракты с оптовиками, импорт, планы логистики ----------
        private static object CollectLogistics(GameInstance g)
        {
            var contracts = new List<object>();
            foreach (var c in g.DeliveryContracts ?? new List<DeliveryContract>())
            {
                if (c == null) continue;
                try
                {
                    contracts.Add(new
                    {
                        wholesaleStreet = c.wholesaleAddress?.streetName, wholesaleNumber = c.wholesaleAddress?.streetNumber ?? 0,
                        street = c.businessAddress?.streetName, number = c.businessAddress?.streetNumber ?? 0,
                        enabled = c.enabled, repeating = c.repeatingOrder, urgent = c.isUrgentOrder,
                        nextDeliveryDay = c.nextDeliveryDay, fee = c.deliveryFee,
                        items = (c.items ?? new List<DeliveryContractItem>()).Where(i => i != null).Select(i => (object)new
                        {
                            item = i.itemName, name = ItemName(i.itemName), amount = i.amount,
                            orderedThisWeek = i.amountOrderedThisWeek,
                            weeklyMax = WeeklyMaxFor != null ? SafeInt(() => WeeklyMaxFor(i.itemName)) : 0, // ItemCached здесь нельзя (фоновый поток)
                            unitPrice = WholesalePrice != null ? Safe(() => WholesalePrice(i.itemName)) : 0f,
                        }).ToList(),
                    });
                }
                catch { }
            }
            var imports = new List<object>();
            foreach (var p in g.importPartnerships ?? new List<ImportPartnership>())
            {
                if (p == null) continue;
                try
                {
                    string agent = null;
                    if (!string.IsNullOrEmpty(p.employeeInstanceId))
                    {
                        var emp = (g.EmployeeInstances ?? new List<EmployeeInstance>())
                            .FirstOrDefault(x => x != null && x.id == p.employeeInstanceId);
                        agent = emp?.characterData?.name;
                    }
                    imports.Add(new
                    {
                        id = p.id,
                        importStreet = p.importAddress?.streetName, importNumber = p.importAddress?.streetNumber ?? 0,
                        active = p.isActive, repeating = p.isRepeatingOrder, urgent = p.isUrgentOrder,
                        smart = p.isTarget,               // «умная доставка»: держать целевой запас на складе
                        agent, nextDeliveryDay = p.nextDeliveryDay,
                        products = (p.products ?? new List<ImportProduct>()).Where(x => x != null).Select(x => (object)new
                        {
                            item = x.itemName, name = ItemName(x.itemName), amount = x.amount,
                            orderedThisWeek = x.amountOrderedThisWeek,
                            warehouseStreet = x.assignedWarehouse?.streetName, warehouseNumber = x.assignedWarehouse?.streetNumber ?? 0,
                        }).ToList(),
                    });
                }
                catch { }
            }
            var plans = new List<object>();
            foreach (var p in g.logisticsManagerPlans ?? new List<Buildings.Office.Headquarters.LogisticsManagerPlan>())
            {
                if (p == null) continue;
                try
                {
                    var dests = p.destinations; // тип не именуем — он в другом namespace
                    var destList = new List<object>();
                    if (dests != null)
                        foreach (var d in dests)
                        {
                            if (d == null) continue;
                            var tg = d.stockTargets;
                            var targets = new List<object>();
                            if (tg != null)
                                foreach (var t in tg)
                                    if (t != null) targets.Add(new { item = t.itemName, name = ItemName(t.itemName), target = t.targetAmount });
                            destList.Add(new { street = d.deliveryTargetAddress?.streetName, number = d.deliveryTargetAddress?.streetNumber ?? 0, targets });
                        }
                    string manager = null;
                    if (!string.IsNullOrEmpty(p.assignedEmployeeId))
                    {
                        var emp = (g.EmployeeInstances ?? new List<EmployeeInstance>())
                            .FirstOrDefault(x => x != null && x.id == p.assignedEmployeeId);
                        manager = emp?.characterData?.name;
                    }
                    plans.Add(new
                    {
                        id = p.id,
                        manager,
                        managerId = p.assignedEmployeeId,
                        isFactory = p.isFactory,
                        maxDestinations = SafeInt(() => MaxDestinationsFor == null ? 0 : MaxDestinationsFor(p.id)),
                        sourceStreet = p.targetAddress?.streetName, sourceNumber = p.targetAddress?.streetNumber ?? 0,
                        destinations = destList,
                    });
                }
                catch { }
            }
            return new { contracts, imports, plans };
        }

        private static int SafeInt(Func<int> f) { try { return f(); } catch { return 0; } }

        private static object CollectWholesalers()
        {
            try { return WholesalersProvider?.Invoke() ?? new List<object>(); } catch { return new List<object>(); }
        }

        // ---------- бизнесы ----------

        private static object CollectBuilding(BuildingRegistration b, List<EmployeeInstance> employeesAtAddr, List<float> dailyProfits)
        {
            int employees = employeesAtAddr?.Count ?? 0;
            var orderDays = new List<object>();
            List<object> lastDayHours = null;
            if (b.orderHistory != null)
            {
                foreach (var o in b.orderHistory.Skip(Math.Max(0, b.orderHistory.Count - 7)))
                {
                    if (o == null) continue;
                    orderDays.Add(new { day = o.dayNumber, customers = o.totalCustomers, revenue = o.totalRevenue });
                }
                var last = b.orderHistory.LastOrDefault(x => x?.hourReports != null);
                if (last != null)
                    lastDayHours = last.hourReports.Select(h => (object)new { hour = h.hour, customers = h.customers }).ToList();
            }

            // Аналитика продаж по товарам за последние дни (из orderHistory).
            var sales = AggregateSales(b, out var soldPerDayByItem);

            // Средний доход и мини-график берём из ежедневной прибыли (financialSummaries),
            // т.к. поле dailyIncomes в файле сохранения не заполнено.
            dailyProfits ??= new List<float>();
            var last7 = dailyProfits.Skip(Math.Max(0, dailyProfits.Count - 7)).ToList();
            float avgDaily = last7.Count > 0 ? last7.Average() : 0f;

            return new
            {
                street = b.StreetName,
                number = b.StreetNumber,
                name = b.BusinessName,
                type = b.businessTypeName,
                streetLabel = SafeStr(() => StreetNameFor == null ? null : StreetNameFor(b.StreetName)),
                allowedSkills = SafeGet(() => AllowedSkillsFor == null ? null : AllowedSkillsFor(AddrKey(b.StreetName, b.StreetNumber))
                    ?.Select(x => ItemName(x)).ToList(), new List<string>()),
                address = SafeStr(() => StreetNameFor == null ? null : b.StreetNumber + " " + StreetNameFor(b.StreetName)),
                hasBusiness = b.HasEstablishedBusiness,
                isWarehouse = b.businessTypeName == "ba:businesstype_warehouse",
                storage = SafeGet(() => WarehouseInfoFor == null ? null : WarehouseInfoFor(AddrKey(b.StreetName, b.StreetNumber)), (object)null),
                rentPerDay = b.RentPerDay,
                temporarilyClosed = b.temporarilyClosed,
                customerCapacity = b.customerCapacity,
                employees,
                avgDailyIncome = avgDaily,
                dailyIncomes = dailyProfits.Skip(Math.Max(0, dailyProfits.Count - 14)).ToList(),
                satisfaction = b.satisfaction == null ? null : new
                {
                    overall = b.satisfaction.overall,
                    customerService = b.satisfaction.customerService,
                    pricing = b.satisfaction.pricing,
                    cleanliness = b.satisfaction.cleanliness,
                    facility = b.satisfaction.facility,
                },
                dirtSpots = b.dirtSpots?.Count ?? 0,
                orderDays,
                lastDayHours,
                sales,
                inventory = CollectInventory(b, soldPerDayByItem),
                staff = CollectStaff(b, employeesAtAddr),
                trafficByHour = CollectTraffic(b),
                workstations = CollectWorkstations(b),
                prices = CollectPrices(b),
                neighborhood = SafeStr(() => b.Neighborhood),
            };
        }

        /// <summary>Средний трафик покупателей по часу суток за последние 7 дней (24 значения).</summary>
        private static List<float> CollectTraffic(BuildingRegistration b)
        {
            var sum = new float[24]; var cnt = new int[24];
            if (b.orderHistory != null)
                foreach (var o in b.orderHistory.Skip(Math.Max(0, b.orderHistory.Count - 7)))
                {
                    if (o?.hourReports == null) continue;
                    foreach (var h in o.hourReports)
                        if (h != null && h.hour >= 0 && h.hour < 24) { sum[h.hour] += h.customers; cnt[h.hour]++; }
                }
            var res = new List<float>();
            for (int i = 0; i < 24; i++) res.Add(cnt[i] > 0 ? (float)Math.Round(sum[i] / cnt[i], 1) : 0f);
            return res;
        }

        /// <summary>Рабочие места — из кэша главного потока (GetAssignableItems/ItemCached небезопасны здесь).</summary>
        private static List<object> CollectWorkstations(BuildingRegistration b)
        {
            if (WorkstationsFor == null) return new List<object>();
            return SafeGet(() => WorkstationsFor(AddrKey(b.StreetName, b.StreetNumber)), new List<object>());
        }

        /// <summary>Цены по товарам — из кэша главного потока (ItemHelper небезопасен здесь).</summary>
        private static List<object> CollectPrices(BuildingRegistration b)
        {
            if (!b.HasEstablishedBusiness || PricesFor == null) return new List<object>();
            return SafeGet(() => PricesFor(AddrKey(b.StreetName, b.StreetNumber)), new List<object>());
        }

        /// <summary>
        /// Расписание магазина, смены, сотрудники и требования по навыкам — для просмотра и
        /// планировщика (панель). Только чтение.
        /// </summary>
        private static object CollectStaff(BuildingRegistration b, List<EmployeeInstance> employees)
        {
            employees ??= new List<EmployeeInstance>();

            // id сотрудника -> имя (для подписи смен)
            var empName = new Dictionary<string, string>();
            foreach (var e in employees)
                if (e != null && !string.IsNullOrEmpty(e.id))
                    empName[e.id] = e.characterData?.name ?? "—";

            // Зона по id предмета-рабочего места (из itemInstances здания).
            string ZoneName(string instanceId)
            {
                if (string.IsNullOrEmpty(instanceId)) return null;
                if (b.itemInstances != null && b.itemInstances.TryGetValue(instanceId, out var inst) && inst != null)
                    return ItemName(inst.itemName);
                return null;
            }

            // Расписание по дням
            var days = new List<object>();
            if (b.scheduleDays != null)
            {
                foreach (var d in b.scheduleDays)
                {
                    if (d == null) continue;
                    var open = (d.openingHourSlots ?? new List<OpeningHourSlot>())
                        .Select(s => (object)new { from = s.startingHour, to = s.endingHour }).ToList();
                    var shifts = (d.workShifts ?? new List<WorkShift>())
                        .Select(s => (object)new
                        {
                            from = s.startingHour,
                            to = s.endingHour,
                            employeeId = s.employeeId,
                            employee = (!string.IsNullOrEmpty(s.employeeId) && empName.TryGetValue(s.employeeId, out var nm)) ? nm : null,
                            zone = ZoneName(s.itemInstanceId),
                            type = s.type.ToString(),
                        }).ToList();
                    days.Add(new
                    {
                        day = d.day.ToString(),
                        dayIndex = (int)d.day,
                        isOpen = d.isOpen,
                        hoursOpen = d.GetHoursOpen,
                        open,
                        shifts,
                    });
                }
            }

            // Сотрудники
            var emps = employees.Where(e => e != null).Select(e =>
            {
                string skill = null;
                try { skill = e.GetPrimarySkill(); } catch { }
                var zones = (e.assignedWorkStationItems ?? new List<string>())
                    .Select(z => ZoneName(z) ?? ItemName(z)).Where(z => !string.IsNullOrEmpty(z)).Distinct().ToList();
                // Все навыки сотрудника с уровнями (обслуживание, уборка, кассир и т.д.)
                var skillList = e.characterData?.skills;
                var skills = skillList == null ? new List<object>()
                    : skillList.Where(s => s != null)
                        .Select(s => (object)new { name = ItemName(s.name), value = (int)Math.Round(s.value) })
                        .ToList();
                return (object)new
                {
                    id = e.id,
                    name = e.characterData?.name ?? "—",
                    skill = string.IsNullOrEmpty(skill) ? null : ItemName(skill),
                    skills,
                    constraints = ResolveConstraints(e),
                    absent = e.isAbsent,      // на больничном / отсутствует
                    replaced = e.isReplaced,  // отсутствующего подменяет замена
                    wage = e.hourlyWage,
                    weeklyHours = e.assignedWeeklyHours,
                    workedThisWeek = e.workedHoursThisWeek,
                    days = e.assignedWeeklyDays == null ? new List<string>() : e.assignedWeeklyDays.Select(x => x.ToString()).ToList(),
                    zones,
                    demands = (e.demands ?? new List<string>()).Select(x => ItemName(x)).ToList(),
                };
            }).ToList();

            // Требуемый скилл-состав под планировку (см. ниже)
            var required = new List<object>();
            try
            {
                var rf = b.GetRequiredSkillForceForLayout();
                if (rf != null)
                    foreach (var f in rf)
                        if (f != null) required.Add(new { skill = ItemName(f.skillName), hours = f.hours });
            }
            catch { }

            return new { scheduleDays = days, employees = emps, required };
        }

        /// <summary>
        /// Разбирает «особенности» сотрудника (job demands) в ограничения для планировщика:
        /// свободные дни (FreeOnDays — напр. выходные), макс./мин. часов в неделю
        /// (HoursWorkingPerWeek), запретные часы смен (HasNoShiftBetweenHours).
        /// </summary>
        private static object ResolveConstraints(EmployeeInstance e)
        {
            var freeDays = new List<string>();
            int? maxHours = null, minHours = null, daysPerWeek = null;
            bool noCleaning = false;
            var noShift = new List<object>();

            if (e.demands != null)
            {
                foreach (var name in e.demands)
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    Entities.Employee.JobDemands.JobDemand jd = null;
                    try { jd = Entities.Employee.JobDemands.JobDemandHelper.GetByName(name); } catch { }
                    if (jd == null) continue;

                    if (jd is Entities.Employee.JobDemands.Requirements.FreeOnDays fod)
                    {
                        if (GetPrivateField(fod, "freeDays") is System.Array arr)
                            foreach (var d in arr) if (d != null) freeDays.Add(d.ToString());
                    }
                    else if (jd is Entities.Employee.JobDemands.Requirements.HoursWorkingPerWeek hw)
                    {
                        try { maxHours = hw.MaxHours; minHours = hw.MinHours; } catch { }
                    }
                    else if (jd is Entities.Employee.JobDemands.Requirements.HasNoShiftBetweenHours)
                    {
                        AddNoShift(noShift, GetPrivateField(jd, "shiftPeriod"));
                        AddNoShift(noShift, GetPrivateField(jd, "nextShiftPeriod"));
                    }
                    else if (jd is Entities.Employee.JobDemands.Requirements.DaysWorkingPerWeek)
                    {
                        // Требование «ровно N рабочих дней в неделю» (Fulfilled: assignedWeeklyDays.Count == N)
                        if (GetPrivateField(jd, "daysWorkingPerWeek") is int n && n > 0) daysPerWeek = n;
                    }
                    else if (jd is Entities.Employee.JobDemands.Requirements.NoSpecificShift)
                    {
                        // «Без смен по уборке»: массив WorkShiftType, в котором есть Cleaning
                        if (GetPrivateField(jd, "workShiftTypes") is System.Array types)
                            foreach (var t in types) if (t != null && t.ToString() == "Cleaning") noCleaning = true;
                    }
                }
            }

            return new
            {
                freeDays = freeDays.Distinct().ToList(),
                maxHours,
                minHours,
                noShiftHours = noShift,
                daysPerWeek,
                noCleaning,
            };
        }

        private static void AddNoShift(List<object> list, object vec)
        {
            if (!(vec is UnityEngine.Vector2Int v)) return;
            if (v.x == 0 && v.y == 0) return;
            list.Add(new { from = v.x, to = v.y });
        }

        private static object GetPrivateField(object obj, string field)
        {
            if (obj == null) return null;
            try
            {
                var f = obj.GetType().GetField(field,
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public);
                return f?.GetValue(obj);
            }
            catch { return null; }
        }

        /// <summary>
        /// Суммирует товары по всем предметам здания (полки, стеллажи, паллеты, коробки) и
        /// добавляет локализованное имя, скорость продаж и прогноз «хватит на N дней».
        /// </summary>
        private static List<object> CollectInventory(BuildingRegistration b, Dictionary<string, float> soldPerDay)
        {
            var totals = new Dictionary<string, (int amount, float value)>();
            // Ёмкость — из кэша главного потока (ItemCached небезопасен в фоновом потоке).
            var bkey = AddrKey(b.StreetName, b.StreetNumber);
            var capacity = CapacityFor != null ? SafeGet(() => CapacityFor(bkey), new Dictionary<string, int>()) : new Dictionary<string, int>();
            int freeCapacity = FreeCapacityFor != null ? SafeInt(() => FreeCapacityFor(bkey)) : 0;
            if (b.itemInstances != null)
            {
                foreach (var inst in b.itemInstances.Values)
                {
                    if (inst?.cargoInstances == null) continue;
                    foreach (var cargo in inst.cargoInstances)
                    {
                        if (cargo == null) continue;
                        AddCargo(totals, cargo.itemName, cargo.amount, cargo.pricePerUnit);
                        if (cargo.nestedCargoInstances == null) continue;
                        foreach (var nested in cargo.nestedCargoInstances)
                        {
                            if (nested == null) continue;
                            AddCargo(totals, nested.itemName, nested.amount, nested.pricePerUnit);
                        }
                    }
                }
            }
            return totals
                .OrderByDescending(kv => kv.Value.amount)
                .Select(kv =>
                {
                    float perDay = soldPerDay != null && soldPerDay.TryGetValue(kv.Key, out var p) ? p : 0f;
                    double? daysLeft = perDay > 0.001f ? (double?)Math.Round(kv.Value.amount / perDay, 1) : null;
                    capacity.TryGetValue(kv.Key, out var cap);
                    return (object)new
                    {
                        item = kv.Key,
                        name = ItemName(kv.Key),
                        amount = kv.Value.amount,
                        value = kv.Value.value,
                        soldPerDay = (float)Math.Round(perDay, 1),
                        daysLeft,
                        capacity = cap,
                        freeCapacity,
                        wholesale = WholesalePrice != null ? Safe(() => WholesalePrice(kv.Key)) : 0f,
                    };
                })
                .ToList();
        }

        /// <summary>
        /// Агрегирует продажи по товарам за последние SalesWindowDays дней (из orderHistory):
        /// продано штук, выручка, себестоимость, прибыль, продаж/день. Также отдаёт скорость
        /// продаж по товарам (для прогноза остатков).
        /// </summary>
        // Сколько последних дней показывать в подневном графике продаж по товару.
        private const int SalesChartDays = 14;

        private static List<object> AggregateSales(BuildingRegistration b, out Dictionary<string, float> soldPerDayByItem)
        {
            soldPerDayByItem = new Dictionary<string, float>();
            var agg = new Dictionary<string, (int sold, float revenue, float wholesale)>();
            // Подневные продажи по товару: itemKey -> (dayNumber -> (sold, revenue, profit))
            var perDayByItem = new Dictionary<string, Dictionary<int, (int sold, float rev, float profit)>>();
            var chartDayNumbers = new List<int>();

            int daysCounted = 0;
            if (b.orderHistory != null)
            {
                var recent = b.orderHistory.Skip(Math.Max(0, b.orderHistory.Count - SalesWindowDays)).ToList();
                daysCounted = recent.Count;
                foreach (var day in recent)
                {
                    if (day?.itemSales == null) continue;
                    foreach (var r in day.itemSales)
                    {
                        if (r == null || string.IsNullOrEmpty(r.itemName)) continue;
                        agg.TryGetValue(r.itemName, out var cur);
                        agg[r.itemName] = (cur.sold + r.amountSold,
                                           cur.revenue + r.totalPrice,
                                           cur.wholesale + r.totalWholesalePrice);
                    }
                }

                // Подневная история (для графика) — отдельным окном.
                var chartDays = b.orderHistory.Skip(Math.Max(0, b.orderHistory.Count - SalesChartDays)).ToList();
                foreach (var day in chartDays)
                {
                    if (day == null) continue;
                    chartDayNumbers.Add(day.dayNumber);
                    if (day.itemSales == null) continue;
                    foreach (var r in day.itemSales)
                    {
                        if (r == null || string.IsNullOrEmpty(r.itemName)) continue;
                        if (!perDayByItem.TryGetValue(r.itemName, out var m))
                            perDayByItem[r.itemName] = m = new Dictionary<int, (int sold, float rev, float profit)>();
                        m.TryGetValue(day.dayNumber, out var s);
                        m[day.dayNumber] = (s.sold + r.amountSold, s.rev + r.totalPrice, s.profit + (r.totalPrice - r.totalWholesalePrice));
                    }
                }
            }

            int div = Math.Max(1, daysCounted);
            foreach (var kv in agg)
                soldPerDayByItem[kv.Key] = (float)kv.Value.sold / div;

            return agg
                .OrderByDescending(kv => kv.Value.revenue)
                .Select(kv =>
                {
                    perDayByItem.TryGetValue(kv.Key, out var m);
                    var daily = chartDayNumbers.Select(dn =>
                    {
                        (int sold, float rev, float profit) v = default;
                        if (m != null) m.TryGetValue(dn, out v);
                        return (object)new { day = dn, sold = v.sold, revenue = v.rev, profit = v.profit };
                    }).ToList();
                    return (object)new
                    {
                        item = kv.Key,
                        name = ItemName(kv.Key),
                        sold = kv.Value.sold,
                        perDay = (float)Math.Round((float)kv.Value.sold / div, 1),
                        revenue = kv.Value.revenue,
                        profit = kv.Value.revenue - kv.Value.wholesale,
                        daily,
                    };
                })
                .ToList();
        }

        private static void AddCargo(Dictionary<string, (int amount, float value)> totals,
            string itemName, int amount, float pricePerUnit)
        {
            if (string.IsNullOrEmpty(itemName) || amount <= 0) return;
            totals.TryGetValue(itemName, out var cur);
            totals[itemName] = (cur.amount + amount, cur.value + amount * pricePerUnit);
        }

        // ---------- проблемы (выводим из сохранённых полей) ----------

        private static void DeriveBuildingProblems(BuildingRegistration b, int employees, List<object> problems)
        {
            if (!b.HasEstablishedBusiness) return;

            if (b.temporarilyClosed)
                Problem(problems, "BusinessTemporarilyClosed", "High", b, null);

            if (employees == 0)
                Problem(problems, "NoEmployees", "High", b, null);

            if (b.dirtSpots != null && b.dirtSpots.Count > 0)
                Problem(problems, "DirtyFloors", "Low", b, null, remainingDays: b.dirtSpots.Count);

            // Остатки на полках, выставленных на продажу
            var forSale = SafeItemsForSale(b);
            if (forSale.Count > 0)
            {
                var totals = InventoryTotals(b);
                foreach (var item in forSale)
                {
                    totals.TryGetValue(item, out int amt);
                    if (amt == 0)
                        Problem(problems, "EmptyStock", "High", b, item);
                    else if (amt <= LowStockThreshold)
                        Problem(problems, "LowStock", "Medium", b, item, remainingDays: amt);
                }
            }

            if (b.satisfaction != null && b.satisfaction.overall > 0 && b.satisfaction.overall < 40)
                Problem(problems, "LowSatisfaction", "Medium", b, null, remainingDays: b.satisfaction.overall);
        }

        private static void DeriveGlobalProblems(GameInstance g,
            Dictionary<string, List<EmployeeInstance>> employeesByAddr, List<object> problems)
        {
            try
            {
                if (g.currentUnpaidTaxes != null && g.currentUnpaidTaxes.totalToPay > 0.01f)
                    problems.Add(new { type = "PayTaxes", priority = "High", street = (string)null, number = 0,
                        item = (string)null, remainingDays = g.currentUnpaidTaxes.dueDay - g.Day });
            }
            catch (Exception) { }

            try
            {
                if (g.currentBackTaxes > 0.01f)
                    problems.Add(new { type = "BackTaxes", priority = "Critical", street = (string)null, number = 0,
                        item = (string)null, remainingDays = 0 });
            }
            catch (Exception) { }

            if (g.EmployeeInstances != null)
            {
                foreach (var e in g.EmployeeInstances)
                {
                    if (e == null) continue;
                    if (e.assignedAddress == null || string.IsNullOrEmpty(e.assignedAddress.streetName))
                        problems.Add(new { type = "EmployeeUnassigned", priority = "Medium",
                            street = (string)null, number = 0, item = e.characterData?.name, remainingDays = 0 });
                    else if (e.satisfaction > 0 && e.satisfaction < 40)
                        problems.Add(new { type = "UnhappyEmployee", priority = "Medium",
                            street = e.assignedAddress.streetName, number = e.assignedAddress.streetNumber,
                            item = e.characterData?.name, remainingDays = (int)e.satisfaction });
                }
            }
        }

        private static void Problem(List<object> problems, string type, string priority,
            BuildingRegistration b, string item, int remainingDays = 0)
        {
            problems.Add(new { type, priority, street = b.StreetName, number = b.StreetNumber, item, remainingDays });
        }

        private static List<string> SafeItemsForSale(BuildingRegistration b)
        {
            try { return b.GetListOfItemsForSale() ?? new List<string>(); }
            catch (Exception) { return new List<string>(); }
        }

        private static Dictionary<string, int> InventoryTotals(BuildingRegistration b)
        {
            var totals = new Dictionary<string, int>();
            if (b.itemInstances == null) return totals;
            foreach (var inst in b.itemInstances.Values)
            {
                if (inst?.cargoInstances == null) continue;
                foreach (var cargo in inst.cargoInstances)
                {
                    if (cargo == null || string.IsNullOrEmpty(cargo.itemName)) continue;
                    totals.TryGetValue(cargo.itemName, out int cur);
                    totals[cargo.itemName] = cur + cargo.amount;
                }
            }
            return totals;
        }

        // ---------- финансы ----------

        private static object CollectFinances(GameInstance g)
        {
            var summaries = new List<object>();
            if (g.financialSummaries != null)
            {
                foreach (var s in g.financialSummaries.Skip(Math.Max(0, g.financialSummaries.Count - 30)))
                {
                    if (s == null) continue;
                    var perBusiness = new List<object>();
                    if (s.businessIncomeStatements != null)
                    {
                        foreach (var st in s.businessIncomeStatements)
                        {
                            if (st == null) continue;
                            perBusiness.Add(new
                            {
                                street = st.Address?.streetName,
                                number = st.Address?.streetNumber ?? 0,
                                sales = st.TotalSales,
                                resources = st.TotalResources,
                                salary = st.SalaryExpenses,
                                rent = st.RentExpenses,
                                marketing = st.MarketingExpenses,
                                theft = st.Theft,
                                licensing = st.LicensingFees,
                                ongoing = st.TotalOngoing,
                                profit = st.TotalProfit,
                            });
                        }
                    }
                    summaries.Add(new { day = s.dayNumber, totalProfit = s.totalBusinessProfit, businesses = perBusiness });
                }
            }

            var weekly = new List<object>();
            if (g.playerWeeklyIncomeHistory != null)
                foreach (var w in g.playerWeeklyIncomeHistory)
                    if (w != null) weekly.Add(new { week = w.Item1, income = w.Item2 });

            return new { dailySummaries = summaries, weeklyIncomeHistory = weekly };
        }

        // ---------- helpers ----------

        private static Dictionary<string, List<EmployeeInstance>> IndexEmployees(GameInstance g)
        {
            var map = new Dictionary<string, List<EmployeeInstance>>();
            if (g.EmployeeInstances == null) return map;
            foreach (var e in g.EmployeeInstances)
            {
                if (e?.assignedAddress == null || string.IsNullOrEmpty(e.assignedAddress.streetName)) continue;
                var key = AddrKey(e.assignedAddress.streetName, e.assignedAddress.streetNumber);
                if (!map.TryGetValue(key, out var lst)) map[key] = lst = new List<EmployeeInstance>();
                lst.Add(e);
            }
            return map;
        }

        private static string AddrKey(string street, int number) => (street ?? "") + "|" + number;

        private static bool SafeCanWrite()
        {
            try { return CanWrite != null && CanWrite(); } catch { return false; }
        }

        /// <summary>История дневной прибыли по каждому адресу бизнеса из financialSummaries.</summary>
        private static Dictionary<string, List<float>> IndexDailyProfits(GameInstance g)
        {
            var map = new Dictionary<string, List<float>>();
            if (g.financialSummaries == null) return map;
            foreach (var s in g.financialSummaries)
            {
                if (s?.businessIncomeStatements == null) continue;
                foreach (var st in s.businessIncomeStatements)
                {
                    if (st?.Address == null) continue;
                    var key = AddrKey(st.Address.streetName, st.Address.streetNumber);
                    if (!map.TryGetValue(key, out var lst)) map[key] = lst = new List<float>();
                    lst.Add(st.TotalProfit);
                }
            }
            return map;
        }
    }
}
