using System;
using System.Collections.Generic;
using System.Linq;
using BigAmbitions.Items;
using Buildings;
using Entities;
using Helpers;

namespace BigAmbitionsMonitor
{
    /// <summary>
    /// Запись в игру: розничные цены и заказы оптовику. Повторяет то, что делает UI игры.
    /// ВСЕ методы вызывать ТОЛЬКО в главном потоке (через MainThreadDispatcher).
    /// </summary>
    public static class GameWriters
    {
        // ---------------- Цены ----------------

        public class PriceDto { public string item; public float price; }
        public class PricesRequest { public string street; public int number; public List<PriceDto> prices; }
        public class SimpleResult { public bool ok; public int changed; public int nextDeliveryDay; public string error; public List<string> warnings = new List<string>(); }

        /// <summary>
        /// Как InventoryProductCellView: цена 0..10000, пишется и в retailPrices, и в storedRetailPrices;
        /// при создании записи — ProductMarketHelper.UpdateMarketDemand(item).
        /// </summary>
        public static SimpleResult SetPrices(PricesRequest req)
        {
            var res = new SimpleResult();
            var g = SaveGameManager.Current;
            if (g == null) { res.error = "сейв не загружен"; return res; }
            var b = FindBuilding(g, req?.street, req?.number ?? 0);
            if (b == null) { res.error = "здание не найдено"; return res; }
            b.retailPrices ??= new List<RetailPrice>();
            b.storedRetailPrices ??= new List<RetailPrice>();

            foreach (var p in req.prices ?? new List<PriceDto>())
            {
                if (p == null || string.IsNullOrEmpty(p.item)) continue;
                float price = Math.Max(0f, Math.Min(10000f, p.price));
                var rp = b.retailPrices.FirstOrDefault(x => x != null && x.itemName == p.item);
                var sp = b.storedRetailPrices.FirstOrDefault(x => x != null && x.itemName == p.item);
                bool created = false;
                if (rp == null) { rp = new RetailPrice { itemName = p.item, price = price }; b.retailPrices.Add(rp); created = true; }
                if (sp == null) { sp = new RetailPrice { itemName = p.item, price = price }; b.storedRetailPrices.Add(sp); }
                rp.price = price; sp.price = price;
                if (created) { try { ProductMarketHelper.UpdateMarketDemand(p.item); } catch (Exception e) { res.warnings.Add("UpdateMarketDemand: " + e.Message); } }
                res.changed++;
            }
            res.ok = true;
            return res;
        }

        // ---------------- Заказ оптовику ----------------

        public class OrderItemDto { public string item; public int amount; }
        public class OrderRequest
        {
            public string street; public int number;                    // бизнес
            public string wholesaleStreet; public int wholesaleNumber;  // оптовик
            public List<OrderItemDto> items;
            public bool? repeating;   // повторяющийся заказ
            public bool? enabled;     // включить контракт
            public bool? urgent;      // срочная доставка
        }

        /// <summary>
        /// Как WholesaleStoreManagerDialog + BizMan Deliveries: один контракт на пару (бизнес, оптовик),
        /// у бизнеса должно быть хранилище (isbusinessstorage), позиции ограничены недельным лимитом
        /// (maxWholesaleOrderAmount - amountOrderedThisWeek), в lock-период менять нельзя.
        /// </summary>
        public static SimpleResult ApplyOrder(OrderRequest req)
        {
            var res = new SimpleResult();
            var g = SaveGameManager.Current;
            if (g == null) { res.error = "сейв не загружен"; return res; }
            var b = FindBuilding(g, req?.street, req?.number ?? 0);
            if (b == null) { res.error = "бизнес не найден"; return res; }
            var wsAddr = new Address(req.wholesaleStreet, req.wholesaleNumber);
            var wsReg = BuildingHelper.GetBuildingRegistration(wsAddr);
            var wsBld = BuildingHelper.GetBuilding(wsAddr);
            var wsSettings = wsBld?.SpecialService?.settings as WholesaleStoreSettings;
            if (wsReg == null || wsSettings == null) { res.error = "оптовик не найден"; return res; }

            bool hasStorage = b.itemInstances != null && b.itemInstances.Values.Any(x => x?.ItemCached != null && SafeHasTag(x.ItemCached, "ba:itemtag_isbusinessstorage"));
            if (!hasStorage) { res.error = "в бизнесе нет хранилища (стеллаж/склад) для приёма доставки"; return res; }

            g.DeliveryContracts ??= new List<DeliveryContract>();
            var bAddr = b.Address;
            var contract = g.DeliveryContracts.FirstOrDefault(c => c != null && c.businessAddress != null && c.wholesaleAddress != null
                && c.businessAddress.streetName == bAddr.streetName && c.businessAddress.streetNumber == bAddr.streetNumber
                && c.wholesaleAddress.streetName == wsAddr.streetName && c.wholesaleAddress.streetNumber == wsAddr.streetNumber);
            bool created = false;
            if (contract == null)
            {
                contract = new DeliveryContract
                {
                    nextDeliveryDay = DeliveryHelper.GetNextDeliveryDay(),
                    wholesaleAddress = wsAddr,
                    businessAddress = bAddr,
                    items = new List<DeliveryContractItem>(),
                    deliveryFee = wsSettings.deliveryFee,
                };
                g.DeliveryContracts.Add(contract);
                created = true;
                try { GameEvent.Invoke("ba:gameevent_newdeliverycontract"); } catch { }
            }
            else if (!DeliveryHelper.CanModifyContract(contract.nextDeliveryDay))
            {
                res.error = "контракт нельзя менять сейчас (период блокировки перед доставкой)"; return res;
            }
            contract.items ??= new List<DeliveryContractItem>();

            var catalog = new HashSet<string>(SafeItemsForSale(wsReg));
            bool limitsOff = false; try { limitsOff = DeliveryHelper.AreWholesaleAndImportLimitsDisabled(); } catch { }

            foreach (var it in req.items ?? new List<OrderItemDto>())
            {
                if (it == null || string.IsNullOrEmpty(it.item)) continue;
                if (catalog.Count > 0 && !catalog.Contains(it.item)) { res.warnings.Add(it.item + ": оптовик не продаёт"); continue; }
                var ci = contract.items.FirstOrDefault(x => x != null && x.itemName == it.item);
                if (ci == null) { ci = new DeliveryContractItem { itemName = it.item, amount = 0 }; contract.items.Add(ci); }
                int amount = Math.Max(0, it.amount);
                if (!limitsOff)
                {
                    int max = 0; try { max = ci.ItemCached.maxWholesaleOrderAmount - ci.amountOrderedThisWeek; } catch { max = amount; }
                    if (amount > max) { res.warnings.Add($"{it.item}: обрезано до недельного лимита {max}"); amount = Math.Max(0, max); }
                }
                ci.amount = amount;
                res.changed++;
            }
            if (req.repeating.HasValue) contract.repeatingOrder = req.repeating.Value;
            contract.enabled = req.enabled ?? (contract.items.Any(x => x != null && x.amount > 0));

            // Дата доставки — как в игре (BizManContractSettings.StartOrder / MakeUrgentOrder):
            // обычный заказ — ближайший понедельник, срочный — завтра. Без этого у ранее
            // существовавшего контракта остаётся старая дата из прошлого, а доставка идёт только при
            // nextDeliveryDay == текущий день (BusinessHelper) — то есть заказ никогда бы не пришёл.
            bool urgent = req.urgent ?? contract.isUrgentOrder;
            if (contract.enabled && contract.HasItemsToDeliver())
            {
                contract.isUrgentOrder = urgent;
                if (urgent) contract.nextDeliveryDay = g.Day + 1;
                else contract.UpdateNextDeliveryDay();
                if (!created) { try { GameEvent.Invoke("ba:gameevent_updateddeliverycontract"); } catch { } }
            }
            else if (req.urgent.HasValue) contract.isUrgentOrder = req.urgent.Value;
            res.nextDeliveryDay = contract.nextDeliveryDay;
            if (created) res.warnings.Insert(0, "создан новый контракт с оптовиком");
            res.ok = true;
            return res;
        }

        // ---------------- helpers ----------------

        private static BuildingRegistration FindBuilding(GameInstance g, string street, int number)
        {
            if (string.IsNullOrEmpty(street)) return null;
            return (g.BuildingRegistrations ?? new List<BuildingRegistration>())
                .FirstOrDefault(x => x != null && x.StreetName == street && x.StreetNumber == number);
        }

        private static List<string> SafeItemsForSale(BuildingRegistration r)
        {
            try { return r.GetListOfItemsForSale() ?? new List<string>(); } catch { return new List<string>(); }
        }

        private static bool SafeHasTag(Item item, string tag)
        {
            try { return item.HasTag(tag); } catch { return false; }
        }
    }
}
