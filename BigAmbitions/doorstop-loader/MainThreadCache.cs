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
        }

        public static volatile Dictionary<string, BuildingData> Buildings = new Dictionary<string, BuildingData>();
        public static volatile Dictionary<string, List<string>> Unfulfilled = new Dictionary<string, List<string>>();
        public static volatile Dictionary<string, float> Wholesale = new Dictionary<string, float>();
        public static volatile Dictionary<string, int> WeeklyMax = new Dictionary<string, int>(); // maxWholesaleOrderAmount по товару
        public static volatile object Wholesalers = new List<object>();
        public static volatile object Delivery = null; // { nextDeliveryDay, lockPeriod, urgentMultiplier, regularDay, regularHour }
        public static DateTime LastRefresh = DateTime.MinValue;
        public static string LastError;

        private static readonly System.Reflection.MethodInfo UnfulfilledMi =
            typeof(EmployeeInstance).GetMethod("GetUnfulfilledDemands",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        public static Func<string, string> Localize = k => k;

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
                                d.prices.Add(new { item, name = Localize(item), price, hasEntry = explicitPrices.ContainsKey(item), market, wholesale = WholesaleOf(item), defaultMarket = def });
                            }
                        }
                    }
                    catch (Exception e) { LastError = "prices: " + e.Message; }
                    buildings[KeyOf(b)] = d;
                }

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

                // атомарная подмена ссылок
                Buildings = buildings; Unfulfilled = unf; Wholesale = wholesale; WeeklyMax = weeklyMax; Wholesalers = wholesalers; Delivery = delivery;
                LastRefresh = DateTime.Now;
            }
            catch (Exception e) { LastError = "refresh: " + e.Message; }
        }
    }
}
