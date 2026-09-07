using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BigAmbitions.Items;
using Buildings.Office.Headquarters;
using Entities;
using Newtonsoft.Json;

namespace BigAmbitionsMonitor
{
    /// <summary>
    /// Автопилот снабжения. Живёт в моде и работает без открытой панели: раз в игровой день
    /// (в заданный час) пересчитывает цепочку «поставщики → склад → магазины» по тем же правилам,
    /// что экран «Снабжение», и либо только записывает предложения в журнал (режим suggest),
    /// либо применяет их (режим apply): цели логисту — каждый день, заказы — в разрешённые дни
    /// недели (по умолчанию суббота, до блокировки воскресенья 20:00), с недельным лимитом бюджета.
    /// ВСЕ методы, кроме Init/Load/Save, вызываются в ГЛАВНОМ ПОТОКЕ.
    /// </summary>
    public static class Autopilot
    {
        public class Config
        {
            public bool enabled;
            public string mode = "suggest";          // suggest | apply
            public int shopDays = 7;                 // запас в магазинах
            public int whDays = 14;                  // запас на складе
            public int hour = 6;                     // час запуска (игровой)
            public string orderDays = "Saturday";    // дни недели для заказов, через запятую
            public float weeklyBudget = 0f;          // 0 = без лимита
            public bool repeating = true;            // заказы повторяющиеся
            public bool autoDest = true;             // пункты назначения выбирать по срочности («кому нужнее»)
        }

        public class Entry { public int day; public int hour; public string kind; public string text; }

        public class RunResult { public bool ok; public string error; public int targets; public int orders; public float cost; public List<string> lines = new List<string>(); }

        private class Persist { public Config config = new Config(); public List<Entry> journal = new List<Entry>(); public int lastRunDay = -1; public float spentThisWeek; public int spentWeek = -1; }

        private static Persist _p = new Persist();
        private static string _path;
        private static Action<string> _log = _ => { };
        private static readonly object Lock = new object();

        public static Config Cfg => _p.config;

        public static void Init(string root, Action<string> log)
        {
            _log = log ?? _log;
            try
            {
                _path = Path.Combine(root ?? ".", "autopilot.json");
                if (File.Exists(_path))
                {
                    var loaded = JsonConvert.DeserializeObject<Persist>(File.ReadAllText(_path));
                    if (loaded != null) { loaded.config ??= new Config(); loaded.journal ??= new List<Entry>(); _p = loaded; }
                }
            }
            catch (Exception e) { _log("autopilot load: " + e.Message); }
        }

        private static void Save()
        {
            try { if (_path != null) File.WriteAllText(_path, JsonConvert.SerializeObject(_p)); }
            catch (Exception e) { _log("autopilot save: " + e.Message); }
        }

        /// <summary>Снимок состояния для панели (читается фоновым потоком — только простые данные).</summary>
        public static object State()
        {
            lock (Lock)
            {
                return new
                {
                    config = _p.config,
                    journal = _p.journal.Skip(Math.Max(0, _p.journal.Count - 60)).Reverse().ToList(),
                    lastRunDay = _p.lastRunDay,
                    spentThisWeek = _p.spentThisWeek,
                };
            }
        }

        public static void SetConfig(Config c)
        {
            if (c == null) return;
            lock (Lock)
            {
                c.mode = c.mode == "apply" ? "apply" : "suggest";
                c.shopDays = Math.Max(1, Math.Min(60, c.shopDays));
                c.whDays = Math.Max(1, Math.Min(90, c.whDays));
                c.hour = Math.Max(0, Math.Min(23, c.hour));
                if (string.IsNullOrEmpty(c.orderDays)) c.orderDays = "Saturday";
                _p.config = c;
                Save();
            }
        }

        private static void Journal(int day, int hour, string kind, string text)
        {
            lock (Lock)
            {
                _p.journal.Add(new Entry { day = day, hour = hour, kind = kind, text = text });
                if (_p.journal.Count > 200) _p.journal.RemoveRange(0, _p.journal.Count - 200);
            }
        }

        /// <summary>ГЛАВНЫЙ ПОТОК. Вызывается периодически; сам решает, пора ли.</summary>
        public static void Tick()
        {
            try
            {
                var c = _p.config;
                if (!c.enabled) return;
                var g = SaveGameManager.Current;
                if (g == null) return;
                if (_p.lastRunDay == g.Day) return;
                if (g.Hour < c.hour) return;
                Run(manual: false);
            }
            catch (Exception e) { _log("autopilot tick: " + e.Message); }
        }

        // ---------------- расчёт и применение ----------------

        private class Src { public string kind; public Address addr; public string name; public HashSet<string> items; public float fee; public bool hasContract; }

        public static RunResult Run(bool manual)
        {
            var res = new RunResult();
            var g = SaveGameManager.Current;
            if (g == null) { res.error = "сейв не загружен"; return res; }
            var c = _p.config;
            bool apply = c.mode == "apply";
            int day = g.Day, hour = g.Hour;
            string dow = "";
            try { dow = TimeHelper.GetDayOfWeek(day).ToString(); } catch { }
            bool orderDay = manual || (c.orderDays ?? "").Split(',').Select(x => x.Trim()).Any(x => string.Equals(x, dow, StringComparison.OrdinalIgnoreCase));
            bool lockPeriod = false; try { lockPeriod = DeliveryHelper.IsLockPeriod(); } catch { }

            // недельный бюджет: неделя = 7 игровых дней от первого дня
            int week = (Math.Max(1, day) - 1) / 7;
            if (_p.spentWeek != week) { _p.spentWeek = week; _p.spentThisWeek = 0f; }
            float budgetLeft = c.weeklyBudget > 0 ? Math.Max(0f, c.weeklyBudget - _p.spentThisWeek) : float.MaxValue;

            var regs = g.BuildingRegistrations ?? new List<BuildingRegistration>();
            var shops = regs.Where(b => b != null && b.RentedByPlayer && b.HasEstablishedBusiness && !(b is Warehouse)
                                        && SafeItems(b).Count > 0).ToList();
            var rates = new Dictionary<string, Dictionary<string, float>>();  // ключ здания → товар → продажи/день
            foreach (var s in shops) rates[Key(s)] = SoldPerDay(s);

            // --- A. цели логисту ---
            var covered = new Dictionary<string, HashSet<string>>();
            var whDemand = new Dictionary<string, Dictionary<string, float>>(); // склад → товар → расход/день
            foreach (var pl in g.logisticsManagerPlans ?? new List<LogisticsManagerPlan>())
            {
                if (pl == null || pl.targetAddress == null) continue;
                var wh = Helpers.BuildingHelper.GetBuildingRegistration(pl.targetAddress) as Warehouse;
                if (wh == null) continue;
                var req = new LogisticsWriter.PlanRequest { planId = pl.id, destinations = new List<LogisticsWriter.DestinationDto>() };
                int targets = 0;
                // Кого везти сегодня: либо пункты из плана, либо (autoDest) самые «голодные» магазины —
                // по минимальному запасу дней среди товаров, которые лежат на этом складе.
                List<BuildingRegistration> destShops;
                int maxDest = 0; try { maxDest = pl.MaxDestinations; } catch { }
                if (c.autoDest && maxDest > 0)
                {
                    var whItems = WarehouseItems(g, wh);
                    var scored = new List<(BuildingRegistration shop, float days)>();
                    foreach (var shop in shops)
                    {
                        if (!rates.TryGetValue(Key(shop), out var r0)) continue;
                        float best = float.MaxValue; bool any = false;
                        foreach (var kv in r0)
                        {
                            if (kv.Value <= 0f || !whItems.Contains(kv.Key)) continue;
                            int st = 0; try { st = Helpers.BuildingHelper.CountTotalResourcesInStock(shop.Address, kv.Key); } catch { }
                            any = true; best = Math.Min(best, st / kv.Value);
                        }
                        if (any) scored.Add((shop, best));
                    }
                    destShops = scored.OrderBy(x => x.days).ThenBy(x => x.shop.BusinessName).Take(maxDest).Select(x => x.shop).ToList();
                    if (scored.Count > maxDest)
                        res.lines.Add($"{wh.BusinessName}: магазинов {scored.Count}, пунктов {maxDest} — сегодня везём {string.Join(", ", destShops.Select(s => s.BusinessName))} (у них меньше всего запаса)");
                }
                else
                {
                    destShops = (pl.destinations ?? new List<LogisticsManagerPlanDestination>())
                        .Where(d => d?.deliveryTargetAddress != null)
                        .Select(d => Helpers.BuildingHelper.GetBuildingRegistration(d.deliveryTargetAddress))
                        .Where(s => s != null).ToList();
                }
                foreach (var shop in destShops)
                {
                    if (!rates.TryGetValue(Key(shop), out var r)) continue;
                    var dto = new LogisticsWriter.DestinationDto { street = shop.StreetName, number = shop.StreetNumber, targets = new List<LogisticsWriter.TargetDto>() };
                    foreach (var kv in r)
                    {
                        if (kv.Value <= 0f) continue;
                        int amount = (int)Math.Ceiling(kv.Value * c.shopDays);
                        dto.targets.Add(new LogisticsWriter.TargetDto { item = kv.Key, amount = amount });
                        if (!covered.TryGetValue(Key(shop), out var set)) covered[Key(shop)] = set = new HashSet<string>();
                        set.Add(kv.Key);
                        if (!whDemand.TryGetValue(Key(wh), out var dem)) whDemand[Key(wh)] = dem = new Dictionary<string, float>();
                        dem.TryGetValue(kv.Key, out var cur); dem[kv.Key] = cur + kv.Value;
                        targets++;
                    }
                    req.destinations.Add(dto);
                }
                if (c.autoDest)
                {
                    // склад держит запас на все магазины, которые он может обслуживать — пункты ротируются;
                    // эти же магазины считаются покрытыми складом (прямой заказ им не нужен)
                    var whItems = WarehouseItems(g, wh);
                    foreach (var d in req.destinations) foreach (var t in d.targets) whItems.Add(t.item);
                    if (!whDemand.TryGetValue(Key(wh), out var demAll)) whDemand[Key(wh)] = demAll = new Dictionary<string, float>();
                    demAll.Clear();
                    foreach (var shop in shops)
                        if (rates.TryGetValue(Key(shop), out var r0))
                            foreach (var kv in r0)
                                if (kv.Value > 0f && whItems.Contains(kv.Key))
                                {
                                    demAll.TryGetValue(kv.Key, out var cur); demAll[kv.Key] = cur + kv.Value;
                                    if (!covered.TryGetValue(Key(shop), out var cs)) covered[Key(shop)] = cs = new HashSet<string>();
                                    cs.Add(kv.Key);
                                }
                }
                res.targets += targets;
                string who = SafeName(g, pl.assignedEmployeeId) ?? "логист";
                if (apply)
                {
                    var r2 = LogisticsWriter.Apply(req);
                    res.lines.Add($"маршрут {who} → {wh.BusinessName}: {(r2.ok ? $"{r2.destinations} пунктов, {r2.targets} целей" : r2.error)}");
                }
                else res.lines.Add($"предложение: маршрут {who} → {wh.BusinessName}: {req.destinations.Count} пунктов, {targets} целей (запас {c.shopDays} дн.)");
            }

            // --- поставщики ---
            var sources = new List<Src>();
            try
            {
                foreach (var bld in Helpers.BuildingHelper.SpecialServiceBuildings.Values)
                {
                    if (bld?.SpecialService?.settings == null) continue;
                    var reg = Helpers.BuildingHelper.GetBuildingRegistration(bld.Address);
                    if (bld.SpecialService.settings is global::Buildings.WholesaleStoreSettings ws)
                        sources.Add(new Src { kind = "wholesale", addr = bld.Address, name = reg?.BusinessName ?? bld.Address.streetName, items = new HashSet<string>(SafeItems(reg)), fee = ws.deliveryFee });
                    else if (bld.SpecialService.settings is global::Buildings.ImportExportSettings ims)
                    {
                        var items = new HashSet<string>();
                        try { foreach (var i in ims.GetItemsAvailable() ?? (IReadOnlyList<string>)new List<string>()) items.Add(i); } catch { }
                        bool hasContract = (g.importPartnerships ?? new List<ImportPartnership>()).Any(x => x != null && x.importAddress == bld.Address);
                        sources.Add(new Src { kind = "import", addr = bld.Address, name = reg?.BusinessName ?? bld.Address.streetName, items = items, hasContract = hasContract });
                    }
                }
            }
            catch (Exception e) { res.lines.Add("поставщики: " + e.Message); }

            if (!orderDay || lockPeriod)
            {
                res.lines.Add(lockPeriod ? "период блокировки — заказы не размещаем" : $"сегодня {dow}: заказы по расписанию только в {c.orderDays}");
            }
            else
            {
                // --- B. пополнение складов ---
                foreach (var kv in whDemand)
                {
                    var wh = regs.FirstOrDefault(b => b != null && Key(b) == kv.Key) as Warehouse;
                    if (wh == null) continue;
                    var groups = new Dictionary<string, (Src src, List<(string item, int amount, float unit)> items)>();
                    foreach (var dem in kv.Value)
                    {
                        string item = dem.Key;
                        // импортёр с контрактом → оптовик с контрактом на этот склад → любой оптовик
                        var src = sources.FirstOrDefault(s => s.kind == "import" && s.hasContract && s.items.Contains(item))
                               ?? sources.FirstOrDefault(s => s.kind == "wholesale" && HasLiveContract(g, s.addr, wh.Address) && s.items.Contains(item))
                               ?? sources.FirstOrDefault(s => s.kind == "wholesale" && HasContract(g, s.addr, wh.Address) && s.items.Contains(item))
                               ?? sources.FirstOrDefault(s => s.kind == "wholesale" && s.items.Contains(item));
                        if (src == null)
                        {
                            var imp = sources.FirstOrDefault(s => s.kind == "import" && s.items.Contains(item));
                            if (imp != null) res.lines.Add($"{wh.BusinessName}: «{ItemLabel(item)}» есть только у импортёра {imp.name} — нужен контракт (звонок в игре)");
                            continue;
                        }
                        int stock = 0; try { stock = Helpers.BuildingHelper.CountResourcesInPallets(wh.Address, item); } catch { }
                        int transit = InTransit(g, wh, item, src);          // без собственного заказа этого источника
                        int was = ExistingAmount(g, src, wh, item);
                        int need = (int)Math.Ceiling(dem.Value * c.whDays) - stock - transit;
                        int amount = Math.Max(0, Math.Min(need, Fit(wh, item, true)));
                        amount = Math.Min(amount, WeeklyLimit(g, src, wh, item));
                        if (amount <= 0 && was <= 0) continue;   // ноль записываем только чтобы убрать лишнее из контракта
                        float unit = UnitPrice(item);
                        if (budgetLeft < float.MaxValue && amount * unit > budgetLeft)
                        {
                            amount = (int)Math.Floor(budgetLeft / Math.Max(0.01f, unit));
                            if (amount <= 0) { res.lines.Add($"бюджет недели исчерпан — «{ItemLabel(item)}» для {wh.BusinessName} пропущен"); continue; }
                        }
                        budgetLeft -= amount * unit;
                        var gkey = src.kind + "|" + src.addr.streetName + "|" + src.addr.streetNumber;
                        if (!groups.TryGetValue(gkey, out var grp)) groups[gkey] = grp = (src, new List<(string, int, float)>());
                        grp.items.Add((item, amount, unit));
                    }
                    foreach (var grp in groups.Values) PlaceOrder(g, grp.src, wh, grp.items, apply, c, res);
                }

                // --- C. прямые заказы магазинам ---
                foreach (var shop in shops)
                {
                    covered.TryGetValue(Key(shop), out var cov);
                    var groups = new Dictionary<string, (Src src, List<(string item, int amount, float unit)> items)>();
                    foreach (var kv in rates[Key(shop)])
                    {
                        string item = kv.Key;
                        if (kv.Value <= 0f || (cov != null && cov.Contains(item))) continue;
                        var src = sources.FirstOrDefault(s => s.kind == "wholesale" && HasLiveContract(g, s.addr, shop.Address) && s.items.Contains(item))
                               ?? sources.FirstOrDefault(s => s.kind == "wholesale" && HasContract(g, s.addr, shop.Address) && s.items.Contains(item))
                               ?? sources.FirstOrDefault(s => s.kind == "wholesale" && s.items.Contains(item));
                        if (src == null) continue;
                        int stock = 0; try { stock = Helpers.BuildingHelper.CountTotalResourcesInStock(shop.Address, item); } catch { }
                        int transit = InTransit(g, shop, item, src);
                        int was = ExistingAmount(g, src, shop, item);
                        int need = (int)Math.Ceiling(kv.Value * c.shopDays) - stock - transit;
                        int amount = Math.Max(0, Math.Min(need, Fit(shop, item, false)));
                        amount = Math.Min(amount, WeeklyLimit(g, src, shop, item));
                        if (amount <= 0 && was <= 0) continue;
                        float unit = UnitPrice(item);
                        if (budgetLeft < float.MaxValue && amount * unit > budgetLeft)
                        {
                            amount = (int)Math.Floor(budgetLeft / Math.Max(0.01f, unit));
                            if (amount <= 0) { res.lines.Add($"бюджет недели исчерпан — «{ItemLabel(item)}» для {shop.BusinessName} пропущен"); continue; }
                        }
                        budgetLeft -= amount * unit;
                        var gkey = src.addr.streetName + "|" + src.addr.streetNumber;
                        if (!groups.TryGetValue(gkey, out var grp)) groups[gkey] = grp = (src, new List<(string, int, float)>());
                        grp.items.Add((item, amount, unit));
                    }
                    foreach (var grp in groups.Values) PlaceOrder(g, grp.src, shop, grp.items, apply, c, res);
                }
            }

            _p.lastRunDay = day;
            res.ok = true;
            Journal(day, hour, apply ? "apply" : "suggest",
                $"{(manual ? "ручной запуск" : "по расписанию")} · целей {res.targets}, заказов {res.orders}, сумма {Math.Round(res.cost)}" +
                (res.lines.Count > 0 ? "\n" + string.Join("\n", res.lines) : ""));
            Save();
            return res;
        }

        private static void PlaceOrder(GameInstance g, Src src, BuildingRegistration target, List<(string item, int amount, float unit)> items, bool apply, Config c, RunResult res)
        {
            bool any = items.Any(x => x.amount > 0);
            float sum = any ? items.Sum(x => x.amount * x.unit) + (src.kind == "wholesale" ? src.fee : 0f) : 0f;
            string list = string.Join(", ", items.Take(6).Select(x => $"{ItemLabel(x.item)} {x.amount}")) + (items.Count > 6 ? "…" : "");
            if (!apply)
            {
                res.orders++; res.cost += sum;
                res.lines.Add($"предложение: {target.BusinessName} ← {src.name}: {list} (~{Math.Round(sum)})");
                return;
            }
            GameWriters.SimpleResult r;
            if (src.kind == "import")
                r = GameWriters.ApplyImportOrder(new GameWriters.ImportRequest
                {
                    street = target.StreetName, number = target.StreetNumber,
                    importStreet = src.addr.streetName, importNumber = src.addr.streetNumber,
                    items = items.Select(x => new GameWriters.ImportItemDto { item = x.item, amount = x.amount }).ToList(),
                    repeating = c.repeating && any, urgent = false, active = any,
                });
            else
                r = GameWriters.ApplyOrder(new GameWriters.OrderRequest
                {
                    street = target.StreetName, number = target.StreetNumber,
                    wholesaleStreet = src.addr.streetName, wholesaleNumber = src.addr.streetNumber,
                    items = items.Select(x => new GameWriters.OrderItemDto { item = x.item, amount = x.amount }).ToList(),
                    repeating = c.repeating && any, urgent = false, enabled = any,
                });
            if (r.ok) { res.orders++; res.cost += sum; _p.spentThisWeek += sum; }
            res.lines.Add($"{target.BusinessName} ← {src.name}: {(r.ok ? $"{list} (~{Math.Round(sum)}), доставка день {r.nextDeliveryDay}" : r.error)}"
                          + (r.warnings.Count > 0 ? " — " + string.Join("; ", r.warnings) : ""));
        }

        // ---------------- helpers ----------------

        private static string Key(BuildingRegistration b) => (b.StreetName ?? "") + "|" + b.StreetNumber;

        /// <summary>Что склад держит или закупает: остаток, включённые контракты на склад, активный импорт на него.</summary>
        private static HashSet<string> WarehouseItems(GameInstance g, Warehouse wh)
        {
            var set = new HashSet<string>();
            foreach (var inst in wh.itemInstances?.Values ?? Enumerable.Empty<ItemInstance>())
                foreach (var ci in inst?.cargoInstances ?? new List<CargoInstance>())
                    if (ci != null && ci.amount > 0 && !string.IsNullOrEmpty(ci.itemName)) set.Add(ci.itemName);
            foreach (var c in g.DeliveryContracts ?? new List<DeliveryContract>())
                if (c != null && c.enabled && c.businessAddress == wh.Address)
                    foreach (var i in c.items ?? new List<DeliveryContractItem>()) if (i != null && i.amount > 0) set.Add(i.itemName);
            foreach (var p in g.importPartnerships ?? new List<ImportPartnership>())
                if (p != null && p.isActive)
                    foreach (var x in p.products ?? new List<ImportProduct>()) if (x != null && x.amount > 0 && x.assignedWarehouse == wh.Address) set.Add(x.itemName);
            return set;
        }

        private static List<string> SafeItems(BuildingRegistration b)
        {
            try { return b?.GetListOfItemsForSale() ?? new List<string>(); } catch { return new List<string>(); }
        }

        private static string SafeName(GameInstance g, string empId)
        {
            if (string.IsNullOrEmpty(empId)) return null;
            return (g.EmployeeInstances ?? new List<EmployeeInstance>()).FirstOrDefault(e => e != null && e.id == empId)?.characterData?.name;
        }

        private static string ItemLabel(string item)
        {
            try { var s = MainThreadCache.Localize(item); return string.IsNullOrEmpty(s) ? item : s; } catch { return item; }
        }

        /// <summary>Продажи/день по товару за последние 7 дней истории заказов (как в снапшоте).</summary>
        private static Dictionary<string, float> SoldPerDay(BuildingRegistration b)
        {
            var result = new Dictionary<string, float>();
            var hist = b.orderHistory;
            if (hist == null || hist.Count == 0) return result;
            var recent = hist.Skip(Math.Max(0, hist.Count - 7)).ToList();
            var sum = new Dictionary<string, int>();
            foreach (var d in recent)
                foreach (var r in d?.itemSales ?? new List<OrderHistoryEntry.ItemReport>())
                    if (r != null && !string.IsNullOrEmpty(r.itemName) && !MainThreadCache.IsService(r.itemName))   // услуги не везут
                    { sum.TryGetValue(r.itemName, out var v); sum[r.itemName] = v + r.amountSold; }
            foreach (var kv in sum) result[kv.Key] = kv.Value / (float)recent.Count;
            return result;
        }

        private static bool HasContract(GameInstance g, Address wholesale, Address business)
        {
            return (g.DeliveryContracts ?? new List<DeliveryContract>()).Any(x => x != null && x.wholesaleAddress == wholesale && x.businessAddress == business);
        }

        /// <summary>Контракт включён и везёт хотя бы одну позицию — его и надо переписывать первым.</summary>
        private static bool HasLiveContract(GameInstance g, Address wholesale, Address business)
        {
            return (g.DeliveryContracts ?? new List<DeliveryContract>()).Any(x => x != null && x.enabled && x.wholesaleAddress == wholesale && x.businessAddress == business
                && (x.items ?? new List<DeliveryContractItem>()).Any(i => i != null && i.amount > 0));
        }

        /// <summary>Уже едет: включённые контракты в это здание + активный импорт, назначенный на этот склад.</summary>
        private static int InTransit(GameInstance g, BuildingRegistration b, string item, Src except = null)
        {
            int n = 0;
            foreach (var c in g.DeliveryContracts ?? new List<DeliveryContract>())
                if (c != null && c.enabled && c.businessAddress == b.Address
                    && !(except != null && except.kind == "wholesale" && c.wholesaleAddress == except.addr))
                    n += (c.items ?? new List<DeliveryContractItem>()).Where(x => x != null && x.itemName == item).Sum(x => x.amount);
            foreach (var p in g.importPartnerships ?? new List<ImportPartnership>())
                if (p != null && p.isActive && !(except != null && except.kind == "import" && p.importAddress == except.addr))
                    n += (p.products ?? new List<ImportProduct>()).Where(x => x != null && x.itemName == item && x.assignedWarehouse == b.Address).Sum(x => x.amount);
            return n;
        }

        /// <summary>Сколько уже стоит в контракте этого источника на это здание (чтобы обнулить лишнее).</summary>
        private static int ExistingAmount(GameInstance g, Src src, BuildingRegistration b, string item)
        {
            if (src.kind == "import")
                return (g.importPartnerships ?? new List<ImportPartnership>()).Where(p => p != null && p.importAddress == src.addr)
                    .SelectMany(p => p.products ?? new List<ImportProduct>()).Where(x => x != null && x.itemName == item && x.assignedWarehouse == b.Address).Sum(x => x.amount);
            return (g.DeliveryContracts ?? new List<DeliveryContract>()).Where(c => c != null && c.wholesaleAddress == src.addr && c.businessAddress == b.Address)
                .SelectMany(c => c.items ?? new List<DeliveryContractItem>()).Where(x => x != null && x.itemName == item).Sum(x => x.amount);
        }

        /// <summary>Сколько влезет одной доставкой — по формуле игры (см. MainThreadCache: слоты × boxSize, домерж в открытые стопки).</summary>
        private static int Fit(BuildingRegistration b, string item, bool warehouse)
        {
            int fit = 0;
            int box = 1; try { box = Math.Max(1, ItemsGetter.GetByName(item)?.boxSize ?? 1); } catch { }
            foreach (var inst in b.itemInstances?.Values ?? Enumerable.Empty<ItemInstance>())
            {
                var it = inst?.ItemCached; if (it == null || it.cargoCapacity <= 0) continue;
                bool ok;
                try
                {
                    int type = (int)it.type;
                    bool shelf = (type & (8 | 0x40)) != 0;
                    string stockItem = inst.cargoInstances?.FirstOrDefault(x => x != null && !string.IsNullOrEmpty(x.itemName))?.itemName;
                    ok = warehouse ? (it.HasTag("ba:itemtag_iswarehousestorage") || it.HasTag("ba:itemtag_isbusinessstorage"))
                                   : (it.HasTag("ba:itemtag_isbusinessstorage") || (shelf && stockItem == item));
                }
                catch { continue; }
                if (!ok) continue;
                int perSlot = (int)Math.Ceiling(box * Math.Max(0.01f, it.cargoCapacityMultiplier));
                var stacks = inst.cargoInstances ?? new List<CargoInstance>();
                fit += Math.Max(0, it.cargoCapacity - stacks.Count) * box;
                foreach (var s in stacks)
                    if (s != null && s.itemName == item) { bool sealedBox = false; try { sealedBox = s.IsSealed; } catch { } if (!sealedBox && (s.nestedCargoInstances == null || s.nestedCargoInstances.Count == 0)) fit += Math.Max(0, perSlot - s.amount); }
            }
            return fit;
        }

        private static int WeeklyLimit(GameInstance g, Src src, BuildingRegistration target, string item)
        {
            try
            {
                if (DeliveryHelper.AreWholesaleAndImportLimitsDisabled()) return int.MaxValue;
                var it = ItemsGetter.GetByName(item);
                if (it == null) return int.MaxValue;
                if (src.kind == "import")
                {
                    if (!DeliveryHelper.ShouldLimitImporterMaxAmount(item, src.addr)) return int.MaxValue;
                    return Math.Max(0, it.maxOrderAmountPerImporter - ImportPartnership.GetItemAmountOrderedThisWeek(src.addr, item));
                }
                var contract = (g.DeliveryContracts ?? new List<DeliveryContract>()).FirstOrDefault(x => x != null && x.wholesaleAddress == src.addr && x.businessAddress == target.Address);
                int ordered = contract?.items?.FirstOrDefault(x => x != null && x.itemName == item)?.amountOrderedThisWeek ?? 0;
                return Math.Max(0, it.maxWholesaleOrderAmount - ordered);
            }
            catch { return int.MaxValue; }
        }

        private static float UnitPrice(string item)
        {
            try { return ItemsGetter.GetByName(item)?.GetWholesalePrice() ?? 0f; } catch { return 0f; }
        }
    }
}
