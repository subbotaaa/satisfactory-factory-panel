using System;
using System.Collections.Generic;
using System.Linq;
using Entities;
using Helpers;

namespace BigAmbitionsMonitor
{
    /// <summary>
    /// Кадровые действия: обучение, перевод между бизнесами, наём и отказ кандидатам.
    /// Повторяет массовые действия игры (UI.Smartphone.Apps.MyEmployees.Types.*), включая их
    /// проверки: обучающегося нельзя переводить, у сотрудника должен быть подходящий навык,
    /// перед обучением игра снимает все смены.
    /// ВСЕ методы вызываются в ГЛАВНОМ ПОТОКЕ (через MainThreadDispatcher).
    /// </summary>
    public static class StaffWriter
    {
        public class Result
        {
            public bool ok;
            public int changed;
            public float cost;
            public string error;
            public List<string> warnings = new List<string>();
        }

        public class TrainRequest { public List<string> employeeIds; }
        public class AssignRequest { public List<string> employeeIds; public string street; public int number; public bool unassign; }
        public class CandidateRequest { public List<string> employeeIds; public bool hire; public string street; public int number; }

        // ---------------- обучение ----------------

        /// <summary>
        /// Отправить на обучение основному навыку — как «Обучить основному навыку» в игре:
        /// прирост до +10 (но не выше 100), списание денег, снятие всех смен, старт сессии.
        /// Обучение заканчивается на следующий день в 17:00.
        /// </summary>
        public static Result Train(TrainRequest req)
        {
            var res = new Result();
            var g = SaveGameManager.Current;
            if (g == null) { res.error = "сейв не загружен"; return res; }
            var emps = FindEmployees(g.EmployeeInstances, req?.employeeIds);
            if (emps.Count == 0) { res.error = "сотрудники не найдены"; return res; }

            foreach (var e in emps)
            {
                var name = e.characterData?.name ?? e.id;
                var skills = e.characterData?.skills;
                if (skills == null || skills.Count == 0) { res.warnings.Add(name + ": нет навыков"); continue; }
                var skill = skills[0];                       // игра обучает именно первому (основному) навыку
                if (!e.CanTrainSkill(skill))
                {
                    res.warnings.Add(name + (e.IsTraining ? ": уже учится" : e.isAbsent ? ": на больничном" : ": навык уже 100"));
                    continue;
                }
                int increase = Math.Min((int)Math.Ceiling(100f - skill.value), 10);
                if (increase <= 0) { res.warnings.Add(name + ": навык уже максимальный"); continue; }

                float cost = 0f;
                try { cost = EmployeeHelper.GetTrainingCost(e, skill.name, increase); } catch { }
                var data = new Dictionary<string, string> { { "employee", name }, { "skillName", skill.name } };
                var info = new TransactionInfo("ba:transaction_employeetraining", data);
                try { info.SetTaxDeductibleName("ba:transaction_employeetraining_label"); } catch { }
                bool paid = false;
                try { paid = GameManager.ChangeMoneySafe(-cost, info); } catch (Exception ex) { res.warnings.Add(name + ": " + ex.Message); }
                if (!paid) { res.warnings.Add(name + ": не хватает денег (" + Math.Round(cost) + ")"); continue; }

                try { EmployeeHelper.UnassignEmployeeFromAllWorkshifts(e); } catch { }
                e.trainingSession = new EmployeeInstance.TrainingInstance { skill = skill.name, startDay = g.Day };
                res.changed++; res.cost += cost;
            }
            try { GameEvent.Invoke(string.Empty); } catch { }
            res.ok = res.changed > 0 || res.warnings.Count == 0;
            if (res.changed == 0 && res.warnings.Count > 0) res.error = "никого не удалось отправить";
            return res;
        }

        // ---------------- перевод между бизнесами ----------------

        /// <summary>
        /// Назначить сотрудников на бизнес или открепить (unassign). Как «Назначить бизнес» в игре:
        /// обучающихся не трогаем, требуем подходящий навык, снимаем смены на прежнем месте.
        /// </summary>
        public static Result Assign(AssignRequest req)
        {
            var res = new Result();
            var g = SaveGameManager.Current;
            if (g == null) { res.error = "сейв не загружен"; return res; }
            var emps = FindEmployees(g.EmployeeInstances, req?.employeeIds);
            if (emps.Count == 0) { res.error = "сотрудники не найдены"; return res; }

            BuildingRegistration target = null;
            if (req != null && !req.unassign)
            {
                target = (g.BuildingRegistrations ?? new List<BuildingRegistration>())
                    .FirstOrDefault(x => x != null && x.StreetName == req.street && x.StreetNumber == req.number);
                if (target == null) { res.error = "бизнес не найден"; return res; }
            }
            var allowed = AllowedSkills(target);
            var newAddress = target?.Address;

            foreach (var e in emps)
            {
                var name = e.characterData?.name ?? e.id;
                if (e.assignedAddress == newAddress) continue;
                if (e.IsTraining) { res.warnings.Add(name + ": на обучении, переводить нельзя"); continue; }
                if (target != null && !HasAnySkill(e, allowed))
                { res.warnings.Add(name + ": нет подходящего навыка для «" + target.BusinessName + "»"); continue; }

                var oldAddress = e.assignedAddress;
                try { EmployeeHelper.UnassignEmployeeFromAllWorkshifts(e); } catch { }
                try { CustomerDemandHelper.ReloadCachedFulfilled(newAddress); } catch { }
                try { CustomerDemandHelper.ReloadCachedFulfilled(oldAddress); } catch { }
                try { e.AddTodoTask(e.IsAssignedToAnyBusiness() ? TodoTaskType.EmployeeIdle : TodoTaskType.EmployeeUnassigned); } catch { }
                e.assignedAddress = newAddress;
                res.changed++;
            }
            try { GameEvent.Invoke(string.Empty); } catch { }
            res.ok = res.changed > 0 || res.warnings.Count == 0;
            if (res.changed == 0 && res.warnings.Count > 0) res.error = "никого не удалось перевести";
            return res;
        }

        // ---------------- кандидаты ----------------

        /// <summary>Нанять кандидата (можно сразу в бизнес) или отказать ему.</summary>
        public static Result Candidates(CandidateRequest req)
        {
            var res = new Result();
            var g = SaveGameManager.Current;
            if (g == null) { res.error = "сейв не загружен"; return res; }
            var cands = FindEmployees(g.CandidateEmployeeInstances, req?.employeeIds);
            if (cands.Count == 0) { res.error = "кандидаты не найдены"; return res; }

            if (req.hire)
            {
                BuildingRegistration target = null;
                if (!string.IsNullOrEmpty(req.street))
                {
                    target = (g.BuildingRegistrations ?? new List<BuildingRegistration>())
                        .FirstOrDefault(x => x != null && x.StreetName == req.street && x.StreetNumber == req.number);
                    if (target == null) { res.error = "бизнес не найден"; return res; }
                }
                var allowed = AllowedSkills(target);
                foreach (var c in cands)
                {
                    var name = c.characterData?.name ?? c.id;
                    if (target != null && !HasAnySkill(c, allowed))
                    { res.warnings.Add(name + ": нет подходящего навыка для «" + target.BusinessName + "»"); continue; }
                    if (target != null) c.assignedAddress = target.Address;
                    try { EmployeeHelper.HireCandidate(c); res.changed++; }
                    catch (Exception ex) { res.warnings.Add(name + ": " + ex.Message); }
                }
            }
            else
            {
                foreach (var c in cands)
                {
                    try { EmployeeHelper.DiscardCandidate(c); res.changed++; }
                    catch (Exception ex) { res.warnings.Add((c.characterData?.name ?? c.id) + ": " + ex.Message); }
                }
            }
            res.ok = res.changed > 0 || res.warnings.Count == 0;
            if (res.changed == 0 && res.warnings.Count > 0) res.error = "ничего не выполнено";
            return res;
        }

        // ---------------- helpers ----------------

        private static List<EmployeeInstance> FindEmployees(List<EmployeeInstance> source, List<string> ids)
        {
            var set = new HashSet<string>((ids ?? new List<string>()).Where(x => !string.IsNullOrEmpty(x)));
            return (source ?? new List<EmployeeInstance>())
                .Where(e => e != null && !string.IsNullOrEmpty(e.id) && set.Contains(e.id)).ToList();
        }

        /// <summary>
        /// Навыки, которые бизнес принимает: основные для его типа плюс уборка, охрана и водитель,
        /// если здание их требует (как в AssignBusinessMassAction).
        /// </summary>
        public static List<string> AllowedSkills(BuildingRegistration reg)
        {
            var list = new List<string>();
            if (reg == null) return list;
            try
            {
                var bt = BusinessTypeHelper.GetData(reg);
                if (bt?.employeePrimarySkills != null) list.AddRange(bt.employeePrimarySkills);
                if (SafeHasTag(bt, "ba:businesstag_allowtheft")) list.Add("ba:skill_securityguard");
            }
            catch { }
            try
            {
                var bd = global::Buildings.BuildingTypeHelper.GetData(reg);
                if (bd != null)
                {
                    if (bd.NeedsCleaning) list.Add("ba:skill_cleaning");
                    if (bd.requiredBuildingSkills != null && bd.requiredBuildingSkills.Contains("ba:skill_deliverydriver"))
                        list.Add("ba:skill_deliverydriver");
                }
            }
            catch { }
            return list.Distinct().ToList();
        }

        private static bool SafeHasTag(object taggedObject, string tag)
        {
            try
            {
                var mi = taggedObject?.GetType().GetMethod("HasTag", new[] { typeof(string) });
                return mi != null && (bool)mi.Invoke(taggedObject, new object[] { tag });
            }
            catch { return false; }
        }

        private static bool HasAnySkill(EmployeeInstance e, List<string> allowed)
        {
            if (allowed == null || allowed.Count == 0) return true;
            var skills = e.characterData?.skills;
            if (skills == null) return false;
            return skills.Any(s => s != null && allowed.Contains(s.name));
        }
    }
}
