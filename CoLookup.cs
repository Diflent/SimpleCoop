namespace SimpleCoop
{
    /// <summary>
    /// Поиск CondOwner по сетевому идентификатору.
    /// В сети передаём strID (уникален и совпадает у хоста и клиента, если загружено одно сохранение).
    /// strName — запасной вариант (имена предметов вроде "ItmSwitch01Off" повторяются!).
    /// </summary>
    public static class CoLookup
    {
        public static string Id(CondOwner? co)
        {
            if (co == null) return "";
            return co.strID ?? co.strName ?? "";
        }

        public static CondOwner? Find(string? id)
        {
            if (string.IsNullOrEmpty(id) || DataHandler.mapCOs == null)
                return null;

            // mapCOs индексирован по strID — O(1)
            if (DataHandler.mapCOs.TryGetValue(id!, out CondOwner co) && co != null)
                return co;

            // fallback: старые пакеты / имя персонажа
            foreach (CondOwner c in DataHandler.mapCOs.Values)
            {
                if (c != null && c.strName == id)
                    return c;
            }

            return null;
        }
    }
}
