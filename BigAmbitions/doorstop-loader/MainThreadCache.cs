using System;
using System.Collections.Generic;
using System.Linq;
using BigAmbitions.Items;
using Entities;

namespace BigAmbitionsMonitor
{
    /// <summary>
    /// Кэш «рискованных» данных, которые можно читать ТОЛЬКО в главном потоке Unity:
    /// цены/рынок/закупка (ItemHelper), рабочие места и ёмкость (ItemCached/GetAssignableItems),
    /// невыполненные требования (JobDemand.Fulfilled), оптовики (Building — MonoBehaviour).
    /// Refresh() выполняется в главном потоке через MainThreadDispatcher раз в несколько секунд;
    /// фоновый сборщик снапшота читает только готовые словари (ссылки заменяются атомарно).
    /// </summary>
    public static class MainThreadCache
    {
        public class PriceRow { public string item; public string name; public float price; public bool hasEntry; public float market; public float wholesale; public float defaultMarket; }
        public class BuildingData
        {
            public List<object> prices = new List<object>();
            public List<object> workstations = new List<object>();
            public Dictionary<string, int> capacity = new Dictionary<string, int>();
            public int freeCapacity;
            // Хранилища: у магазина — isbusinessstorage, у склада — iswarehousestorage.
            // Оптовик кладёт товар только в isbusinessstorage (BusinessHelper.DeliverCargoToBuilding).
            public bool storageBusiness, storageWarehouse;
            public List<object> vehicles;   // слоты машин склада: машина + водитель
            public List<string> allowedSkills = new List<string>();  // кого можно назначить в этот бизнес
            // Контейнеры хранения: слоты, множитель вместимости, стопки товара. Панель по ним считает,
            // сколько ещё влезет каждого товара (у разных палет множитель разный).
            public List<object> containers = new List<object>();
        }

        public static volatile Dictionary<string, BuildingData> Buildings = new Dictionary<string, BuildingData>();
        public static volatile Dictionary<string, List<string>> Unfulfilled = new Dictionary<string, List<string>>();
        public static volatile Dictionary<string, float> Wholesale = new Dictionary<string, float>();
        public static volatile Dictionary<string, float> TrainingCost = new Dictionary<string, float>(); // по id сотрудника
        public static volatile Dictionary<string, int> MaxDestinations = new Dictionary<string, int>();  // по id плана логиста
        public static volatile Dictionary<string, int> BoxSizes = new Dictionary<string, int>();        // товар → размер коробки
        public static volatile HashSet<string> Services = new HashSet<string>();                     // услуги (ItemType.ServiceProduct) — не доставляются
        public static bool IsService(string item) { try { return item != null && Services.Contains(item); } catch { return false; } }
        public static volatile Dictionary<string, int> WeeklyMax = new Dictionary<string, int>(); // maxWholesaleOrderAmount по товару
        public static volatile object Wholesalers = new List<object>();
        public static volatile object Importers = new List<object>();  // импортёры (заказ через агента по закупкам)
        public static volatile object Rentals = new List<object>();   // свободные здания в аренду
        private static int _rentalTick;                                // каталог меняется редко — раз в ~30 с
        public static volatile object Delivery = null; // { nextDeliveryDay, lockPeriod, urgentMultiplier, regularDay, regularHour }
        public static DateTime LastRefresh = DateTime.MinValue;
        public static string LastError;

        private static readonly System.Reflection.MethodInfo UnfulfilledMi =
            typeof(EmployeeInstance).GetMethod("GetUnfulfilledDemands",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        public static Func<string, string> Localize = k => k;
        // Имена улиц лежат не в локализации, а в StreetData (AddressHelper) — «ba:street_fifthavenue» → «5th Avenue».
        public static volatile Dictionary<string, string> StreetNames = new Dictionary<string, string>();
        public static string StreetNameOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            try { return Streets.AddressHelper.GetStreetNameLocalized(key) ?? key; } catch { return key; }
        }
        private static float SafeFloat(Func<float> f) { try { return f(); } catch { return 0f; } }
        private static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }

        /// <summary>Имя владельца здания: конкурент или город (пустой id).</summary>
        private static string OwnerName(BuildingRegistration b)
        {
            try
            {
                var id = b.buildingOwnerRivalId;
                if (string.IsNullOrEmpty(id)) return null;
                var data = BigAmbitions.Rivals.RivalsHelper.GetRivalData(id);
                return data?.rivalName;
            }
            catch { return null; }
        }

        /// <summary>
        /// Игра запрещает аренду, если здание принадлежит активному «особому» конкуренту
        /// (BizManPresentation: notification_cannot_rent_building_owned_by_rival).
        /// </summary>
        private static bool OwnerBlocksRent(BuildingRegistration b)
        {
            try
            {
                if (!BigAmbitions.Rivals.RivalsHelper.IsFeatureEnabled) return false;
                if (b.BuildingOwnedByPlayer) return false;
                var sr = BigAmbitions.Rivals.RivalsHelper.GetSpecialRival(b.buildingOwnerRivalId);
                if (sr == null) return false;
                var state = BigAmbitions.Rivals.RivalsHelper.GetSpecialRivalState(sr.rivalData.id);
                return state != null && state.isActive;
            }
            catch { return false; }
        }

        private static string Key(string street, int number) => (street ?? "") + "|" + number;
        public static string KeyOf(BuildingRegistration b) => Key(b.StreetName, b.StreetNumber);

        /// <summary>ГЛАВНЫЙ ПОТОК. Пересчитать кэш.</summary>
        public static void Refresh()
        {
            try
            {
                var g = SaveGameManager.Current;
                if (g == null) return;

                var wholesale = new Dictionary<string, float>();
                var weeklyMax = new Dictionary<string, int>();
                float WholesaleOf(string item)
                {
                    if (string.IsNullOrEmpty(item)) return 0f;
                    if (wholesale.TryGetValue(item, out var w)) return w;
                    float v = 0f;
                    try
                    {
                        var it = ItemsGetter.AllItems?.FirstOrDefault(x => x != null && x.itemName == item);
                        if (it != null) { v = it.GetWholesalePrice(); weeklyMax[item] = it.maxWholesaleOrderAmount; }
                    }
                    catch { }
                    wholesale[item] = v; return v;
                }

                var buildings = new Dictionary<string, BuildingData>();
                foreach (var b in g.BuildingRegistrations ?? new List<BuildingRegistration>())
                {
                    if (b == null || !b.RentedByPlayer) continue;
                    var d = new BuildingData();
                    try
                    {
                        // ёмкость контейнеров по товару + свободная ёмкость пустых
                        if (b.itemInstances != null)
                            foreach (var inst in b.itemInstances.Values)
                            {
                                if (inst == null) continue;
                                int cap = 0; try { cap = inst.ItemCached?.cargoCapacity ?? 0; } catch { }
                                if (inst.cargoInstances == null || inst.cargoInstances.Count == 0) { if (cap > 0) d.freeCapacity += cap; continue; }
                                var seen = new HashSet<string>();
                                foreach (var c in inst.cargoInstances)
                                    if (c != null && !string.IsNullOrEmpty(c.itemName) && seen.Add(c.itemName))
                                    { d.capacity.TryGetValue(c.itemName, out var c0); d.capacity[c.itemName] = c0 + cap; }
                            }
                    }
                    catch (Exception e) { LastError = "capacity: " + e.Message; }
                    try
                    {
                        if (b.itemInstances != null)
                            foreach (var inst in b.itemInstances.Values)
                            {
                                var it = inst?.ItemCached;
                                if (it == null || it.cargoCapacity <= 0) continue;
                                bool business = false, warehouse = false;
                                try { business = it.HasTag("ba:itemtag_isbusinessstorage"); warehouse = it.HasTag("ba:itemtag_iswarehousestorage"); } catch { }
                                int type = 0; try { type = (int)it.type; } catch { }
                                bool shelf = (type & (8 | 0x40)) != 0;        // PointOfSale | ShowcaseShelf — полки принимают свой товар
                                bool storageShelf = (type & 0x20) != 0;        // StorageShelf — сюда возит логист
                                if (!business && !warehouse && !shelf && !storageShelf) continue;
                                var stacks = new List<object>();
                                foreach (var c in inst.cargoInstances ?? new List<CargoInstance>())
                                {
                                    if (c == null) continue;
                                    bool sealedBox = false; try { sealedBox = c.IsSealed; } catch { }
                                    bool nested = false; try { nested = c.nestedCargoInstances != null && c.nestedCargoInstances.Count > 0; } catch { }
                                    stacks.Add(new { item = c.itemName, amount = c.amount, isSealed = sealedBox, nested });
                                }
                                string stockItem = null;
                                foreach (var c in inst.cargoInstances ?? new List<CargoInstance>()) if (c != null && !string.IsNullOrEmpty(c.itemName)) { stockItem = c.itemName; break; }
                                d.containers.Add(new
                                {
                                    id = inst.id, name = Localize(inst.itemName),
                                    slots = it.cargoCapacity, mult = it.cargoCapacityMultiplier,
                                    business, warehouse, shelf, storageShelf, stockItem, stacks,
                                });
                            }
                    }
                    catch (Exception e) { LastError = "containers: " + e.Message; }
                    try
                    {
                        if (b.itemInstances != null)
                            foreach (var inst in b.itemInstances.Values)
                            {
                                var it = inst?.ItemCached; if (it == null) continue;
                                if (it.HasTag("ba:itemtag_isbusinessstorage")) d.storageBusiness = true;
                                if (it.HasTag("ba:itemtag_iswarehousestorage")) d.storageWarehouse = true;
                            }
                    }
                    catch (Exception e) { LastError = "storage tags: " + e.Message; }
                    try
                    {
                        if (b is Warehouse wh && wh.vehicleSlots != null)
                        {
                            var slots = new List<object>();
                            foreach (var slot in wh.vehicleSlots)
                            {
                                if (slot == null) continue;
                                string vname = null; int cargo = 0;
                                var vi = (g.VehicleInstances ?? new List<VehicleInstance>()).FirstOrDefault(x => x != null && x.id == slot.vehicleInstanceId);
                                if (vi != null)
                                {
                                    vname = Localize(vi.vehicleTypeName);
                                    try { cargo = vi.VehicleType?.maxCargoCapacity ?? 0; } catch { }
                                }
                                string driver = null;
                                if (!string.IsNullOrEmpty(slot.employeeDriverId))
                                {
                                    var emp = (g.EmployeeInstances ?? new List<EmployeeInstance>())
                                        .FirstOrDefault(x => x != null && x.id == slot.employeeDriverId);
                                    driver = emp?.characterData?.name;
                                }
                                int dests = 0; try { dests = slot.DestinationsThatCanDeliver; } catch { }
                                slots.Add(new { vehicle = vname, cargo, driver, destinations = dests });
                            }
                            d.vehicles = slots;
                        }
                    }
                    catch (Exception e) { LastError = "vehicles: " + e.Message; }
                    try { d.allowedSkills = StaffWriter.AllowedSkills(b) ?? new List<string>(); }
                    catch (Exception e) { LastError = "allowed skills: " + e.Message; }
                    try
                    {
                        foreach (var ws in b.GetAssignableItems() ?? new List<ItemInstance>())
                        {
                            if (ws == null) continue;
                            bool cleaning = false; try { cleaning = ws.ItemCached != null && ws.ItemCached.HasTag("ba:itemtag_iscleaningstation"); } catch { }
                            // Навыки, под которые подходит место (локализованные — как e.skill в снапшоте):
                            // панель по ним считает число касс для должности, а не «всё, что не уборка».
                            var skills = new List<string>();
                            try { foreach (var s in ws.ItemCached?.suitableSkills ?? new string[0]) if (!string.IsNullOrEmpty(s)) skills.Add(Localize(s)); } catch { }
                            d.workstations.Add(new { id = ws.id, name = Localize(ws.itemName), cleaning, skills });
                        }
                    }
                    catch (Exception e) { LastError = "workstations: " + e.Message; }
                    try
                    {
                        if (b.HasEstablishedBusiness)
                        {
                            var explicitPrices = new Dictionary<string, float>();
                            foreach (var rp in b.retailPrices ?? new List<RetailPrice>())
                                if (rp != null && !string.IsNullOrEmpty(rp.itemName)) explicitPrices[rp.itemName] = rp.price;
                            string hood = null; try { hood = b.Neighborhood; } catch { }
                            List<string> items = new List<string>(); try { items = b.GetListOfItemsForSale() ?? new List<string>(); } catch { }
                            foreach (var item in items)
                            {
                                if (string.IsNullOrEmpty(item)) continue;
                                float price = 0f, market = 0f, def = 0f;
                                try { price = ItemHelper.GetPrice(item, b); } catch { }
                                try { market = ItemHelper.GetLowestMarketPrice(item, hood, false); } catch { }
                                try { def = ItemHelper.GetDefaultMarketPrice(item); } catch { }
                                // с чем сравнивает покупатель: эталон = min(базовая, минимум конкурентов);
                                // оптимум — базовая × индекс цен района; потолок — эталон × терпимость самого бедного класса
                                float reference = 0f, optimal = 0f, maxAcceptable = 0f;
                                try { reference = ItemHelper.GetMarketReferencePrice(item, hood); } catch { }
                                try { optimal = ItemHelper.CalculateOptimalPriceByNeighborhood(item, hood, ItemHelper.HasPlayerMonopoly(item, hood)); } catch { }
                                try { maxAcceptable = ItemHelper.CalculateMaxAcceptablePriceByNeighborhood(item, hood); } catch { }
                                d.prices.Add(new { item, name = Localize(item), price, hasEntry = explicitPrices.ContainsKey(item), market, wholesale = WholesaleOf(item), defaultMarket = def, reference, optimal, maxAcceptable });
                            }
                        }
                    }
                    catch (Exception e) { LastError = "prices: " + e.Message; }
                    buildings[KeyOf(b)] = d;
                }

                // стоимость обучения основному навыку (SkillHelper/ItemHelper — только главный поток)
                var trainCost = new Dictionary<string, float>();
                foreach (var e in g.EmployeeInstances ?? new List<EmployeeInstance>())
                {
                    if (e?.characterData?.skills == null || e.characterData.skills.Count == 0 || string.IsNullOrEmpty(e.id)) continue;
                    try
                    {
                        var sk = e.characterData.skills[0];
                        int inc = Math.Min((int)Math.Ceiling(100f - sk.value), 10);
                        if (inc > 0) trainCost[e.id] = Helpers.EmployeeHelper.GetTrainingCost(e, sk.name, inc);
                    }
                    catch { }
                }
                TrainingCost = trainCost;

                // размеры коробок всех товаров — по ним панель считает, что влезет в слот
                try
                {
                    var boxes = new Dictionary<string, int>();
                    var services = new HashSet<string>();
                    foreach (var it in ItemsGetter.AllItems ?? new List<Item>())
                    {
                        if (it == null || string.IsNullOrEmpty(it.itemName)) continue;
                        boxes[it.itemName] = Math.Max(1, it.boxSize);
                        try { if (((int)it.type & 4) != 0) services.Add(it.itemName); } catch { }   // ServiceProduct = 4
                    }
                    BoxSizes = boxes; Services = services;
                }
                catch (Exception e) { LastError = "boxsizes: " + e.Message; }

                // лимит пунктов назначения: машины склада + навык логиста
                var maxDest = new Dictionary<string, int>();
                foreach (var pl in g.logisticsManagerPlans ?? new List<Buildings.Office.Headquarters.LogisticsManagerPlan>())
                {
                    if (pl == null || string.IsNullOrEmpty(pl.id)) continue;
                    try { maxDest[pl.id] = pl.MaxDestinations; } catch { }
                }
                MaxDestinations = maxDest;

                var unf = new Dictionary<string, List<string>>();
                foreach (var e in g.EmployeeInstances ?? new List<EmployeeInstance>())
                {
                    if (e == null || string.IsNullOrEmpty(e.id)) continue;
                    try
                    {
                        var list = UnfulfilledMi?.Invoke(e, null) as List<string> ?? new List<string>();
                        unf[e.id] = list.Select(x => Localize(x)).ToList();
                    }
                    catch { unf[e.id] = new List<string>(); }
                }

                // товары из контрактов и складов — тоже нужна закупочная цена
                foreach (var c in g.DeliveryContracts ?? new List<DeliveryContract>())
                    foreach (var i in c?.items ?? new List<DeliveryContractItem>()) if (i != null) WholesaleOf(i.itemName);
                foreach (var b in g.BuildingRegistrations ?? new List<BuildingRegistration>())
                    if (b != null && b.RentedByPlayer && b.itemInstances != null)
                        foreach (var inst in b.itemInstances.Values)
                            foreach (var c in inst?.cargoInstances ?? new List<CargoInstance>()) if (c != null) WholesaleOf(c.itemName);

                object wholesalers = new List<object>();
                try
                {
                    var wl = new List<object>();
                    foreach (var bld in Helpers.BuildingHelper.SpecialServiceBuildings.Values)
                    {
                        if (bld == null) continue;
                        if (!(bld.SpecialService?.settings is Buildings.WholesaleStoreSettings ws)) continue;
                        var addr = bld.Address;
                        var reg = Helpers.BuildingHelper.GetBuildingRegistration(addr);
                        List<string> items = new List<string>(); try { items = reg?.GetListOfItemsForSale() ?? new List<string>(); } catch { }
                        wl.Add(new
                        {
                            street = addr.streetName, number = addr.streetNumber,
                            name = !string.IsNullOrEmpty(reg?.BusinessName) ? reg.BusinessName : Localize(addr.streetName) + ", " + addr.streetNumber,
                            fee = ws.deliveryFee,
                            catalog = items.Select(i => new { item = i, name = Localize(i), wholesale = WholesaleOf(i) }).ToList(),
                        });
                    }
                    wholesalers = wl;
                }
                catch (Exception e) { LastError = "wholesalers: " + e.Message; }

                // Импортёры: каталог у них свой (ImportExportSettings.GetItemsAvailable),
                // недельный лимит на товар — maxOrderAmountPerImporter минус уже заказанное.
                object importers = new List<object>();
                try
                {
                    var il = new List<object>();
                    foreach (var bld in Helpers.BuildingHelper.SpecialServiceBuildings.Values)
                    {
                        if (bld == null) continue;
                        if (!(bld.SpecialService?.settings is global::Buildings.ImportExportSettings ims)) continue;
                        var addr = bld.Address;
                        var reg = Helpers.BuildingHelper.GetBuildingRegistration(addr);
                        var catalog = new List<object>();
                        try
                        {
                            foreach (var item in ims.GetItemsAvailable() ?? (System.Collections.Generic.IReadOnlyList<string>)new List<string>())
                            {
                                if (string.IsNullOrEmpty(item)) continue;
                                int max = 0, week = 0;
                                try { max = ItemsGetter.GetByName(item)?.maxOrderAmountPerImporter ?? 0; } catch { }
                                try { week = ImportPartnership.GetItemAmountOrderedThisWeek(addr, item); } catch { }
                                catalog.Add(new { item, name = Localize(item), wholesale = WholesaleOf(item), weeklyMax = max, orderedThisWeek = week });
                            }
                        }
                        catch (Exception e) { LastError = "importer catalog: " + e.Message; }
                        il.Add(new
                        {
                            street = addr.streetName, number = addr.streetNumber,
                            name = !string.IsNullOrEmpty(reg?.BusinessName) ? reg.BusinessName : Localize(addr.streetName) + ", " + addr.streetNumber,
                            catalog,
                        });
                    }
                    importers = il;
                }
                catch (Exception e) { LastError = "importers: " + e.Message; }
                Importers = importers;

                // Сроки доставки: у всех оптовиков одинаково — понедельник 08:00; срочная — завтра, вся сумма × множитель.
                object delivery = null;
                try
                {
                    delivery = new
                    {
                        nextDeliveryDay = DeliveryHelper.GetNextDeliveryDay(),
                        lockPeriod = DeliveryHelper.IsLockPeriod(),
                        urgentMultiplier = DeliveryHelper.GetWholesaleUrgentFeeMultiplier(),
                        urgentNextDay = g.Day + 1,
                        regularDay = DeliveryHelper.DeliveryDay.ToString(),
                        regularHour = DeliveryHelper.DeliveryHour,
                    };
                }
                catch (Exception e) { LastError = "delivery: " + e.Message; }

                // Каталог аренды: перебирает все здания города, поэтому обновляем реже остальных данных.
                if (_rentalTick++ % 6 == 0)
                {
                    try
                    {
                        var rent = new List<object>();
                        foreach (var b in g.BuildingRegistrations ?? new List<BuildingRegistration>())
                        {
                            if (b == null || !b.AvailableForRent || b.RentedByPlayer) continue;
                            global::Buildings.Building bld = null;
                            try { bld = b.BuildingCached; } catch { }
                            if (bld == null) continue;
                            int sqm = 0, capacity = 0;
                            try { sqm = global::Buildings.BuildingSizeHelper.GetData(bld.BuildingSize)?.squareMeters ?? bld.totalSqm; } catch { sqm = bld.totalSqm; }
                            try { capacity = bld.GetCustomerCapacity; } catch { }
                            float rentDay = b.RentPerDay;
                            if (rentDay <= 0f) { try { rentDay = bld.GetBuildingDailyMarketRent(); } catch { } }
                            rent.Add(new
                            {
                                street = b.StreetName,
                                number = b.StreetNumber,
                                streetName = StreetNameOf(b.StreetName),
                                address = b.StreetNumber + " " + StreetNameOf(b.StreetName),
                                type = bld.BuildingType,
                                typeName = Localize(bld.BuildingType),
                                neighborhood = bld.Neighbourhood,
                                neighborhoodName = Localize(bld.Neighbourhood),
                                size = bld.BuildingSize,
                                sizeName = Localize(bld.BuildingSize),
                                sqm,
                                capacity,
                                traffic = bld.trafficIndex,
                                rentPerDay = rentDay,
                                marketValue = SafeFloat(() => bld.GetMarketValue()),
                                owner = OwnerName(b),
                                ownerBlocked = OwnerBlocksRent(b),   // особый конкурент-владелец аренду не даёт
                                ownedByPlayer = SafeBool(() => b.BuildingOwnedByPlayer),
                                dlc = bld.requiredDLC.ToString(),
                            });
                        }
                        Rentals = rent;
                    }
                    catch (Exception e) { LastError = "rentals: " + e.Message; }
                }

                // имена улиц для всех зданий игрока — панель показывает адреса как в игре
                try
                {
                    var streets = new Dictionary<string, string>();
                    foreach (var b in g.BuildingRegistrations ?? new List<BuildingRegistration>())
                        if (b != null && !string.IsNullOrEmpty(b.StreetName) && !streets.ContainsKey(b.StreetName))
                            streets[b.StreetName] = StreetNameOf(b.StreetName);
                    StreetNames = streets;
                }
                catch (Exception e) { LastError = "streets: " + e.Message; }

                // атомарная подмена ссылок
                Buildings = buildings; Unfulfilled = unf; Wholesale = wholesale; WeeklyMax = weeklyMax; Wholesalers = wholesalers; Delivery = delivery;
                LastRefresh = DateTime.Now;
            }
            catch (Exception e) { LastError = "refresh: " + e.Message; }
        }
    }
}
