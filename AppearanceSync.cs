using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Ostranauts.Core;
using UnityEngine;

namespace SimpleCoop
{
    /// <summary>
    /// Внешность персонажей.
    ///
    /// Окно — то же, что у зеркала раковины и при создании персонажа (GUIChargenBody).
    /// Вне создания персонажа игра прячет в нём большинство кнопок (лицо, кожа, тело...),
    /// поэтому, когда окно открываем мы, патч на GetCOInfo включает их обратно.
    /// Имя не меняется: ChangeName переписывает strID, а на нём держится вся синхронизация.
    ///
    /// Клиент: окно открывается при первом подключении (и по F4), при закрытии внешность
    ///         уходит хосту пакетом APPEARANCE.
    /// Хост:   проверяет, что это персонаж клиента, применяет и рассылает всем.
    ///         Свою внешность хост тоже может поменять по F4 — она разойдётся клиентам.
    /// </summary>
    public static class AppearanceSync
    {
        public const string Msg = "APPEARANCE";

        /// <summary>Временный пункт GUI у персонажа — чтобы CrewSim.RaiseUI открыл GUIChargenBody.</summary>
        private const string GuiKey = "SimpleCoopBody";

        /// <summary>Условия, которые меняет окно: местоимения и предпочтения (см. GUIChargenBody).</summary>
        private static readonly string[] ManagedConds =
        {
            "IsMale", "IsFemale", "IsNB",
            "IsAttractedMen", "IsAttractedWomen", "IsAttractedNB"
        };

        /// <summary>Кнопки, которые GUIChargenBody.GetCOInfo прячет вне создания персонажа. Имя — не трогаем.</summary>
        internal static readonly string[] HiddenButtons =
        {
            "btnRandFace",
            "btnEyes", "btnSkin", "btnLips", "btnNeck", "btnNose", "btnPupils", "btnTeeth", "btnHead", "btnBody",
            "btnEyesBack", "btnSkinBack", "btnLipsBack", "btnNeckBack", "btnNoseBack", "btnPupilsBack",
            "btnTeethBack", "btnHeadBack", "btnBodyBack"
        };

        /// <summary>Окно открыли мы (а не зеркало раковины) — включить все кнопки.</summary>
        internal static bool FullEditor;

        private static CondOwner? _editing;
        private static float _openTimer = -1f;

        // ===================================================================
        //                          Открыть / закрыть
        // ===================================================================

        /// <summary>Открыть окно через delay секунд (после загрузки мира игре нужно «устояться»).</summary>
        public static void RequestOpen(float delay = 1.5f)
        {
            _openTimer = delay;
        }

        /// <summary>Вызывать каждый кадр.</summary>
        public static void Update()
        {
            if (_openTimer < 0f) return;

            _openTimer -= Time.unscaledDeltaTime;
            if (_openTimer > 0f) return;

            _openTimer = -1f;
            OpenEditor();
        }

        public static void OpenEditor()
        {
            if (!NetworkManager.IsInGame() || CrewSim.objInstance == null) return;

            var net = NetworkManager.Current;
            bool isClient = net != null && net.IsRunning && net.Role == NetworkManager.NetRole.Client;

            if (isClient && !net!.IsWorldReady) return;

            // Клиент — свой назначенный персонаж, хост (или одиночная игра) — свой coPlayer
            CondOwner? crew = isClient ? PlayerRegistry.OwnCrew : CrewSim.coPlayer;
            if (crew == null || crew.Crew == null)
            {
                GameLog.Warn("[Look] No character to edit");
                return;
            }

            // У хоста персонаж живой: если он чем-то занят, окно возьмёт не того персонажа
            // (GUIChargenBody читает GetInteractionCurrent().objThem). У клиента очередь
            // персонажа не исполняется — там подменяем всегда.
            if (!isClient && crew.GetInteractionCurrent() != null)
            {
                GameLog.Warn("[Look] Персонаж занят — дождитесь окончания действия");
                return;
            }

            if (crew.mapGUIPropMaps == null)
                crew.mapGUIPropMaps = new Dictionary<string, Dictionary<string, string>>();

            // Как у зеркала раковины (guipropmaps.json, "ChargenBodySink")
            crew.mapGUIPropMaps[GuiKey] = new Dictionary<string, string>
            {
                { "strGUIPrefab", "GUIChargenBody" },
                { "strFriendlyName", "Appearance" },
                { "strGUIPrefabRight", null! },
                { "strGUIPrefabLeft", null! },
                { "strGUIPrefabUp", null! },
                { "strGUIPrefabDown", null! }
            };

            // Окно показывается для выделенного персонажа
            if (crew != CrewSim.GetSelectedCrew())
                CrewSim.objInstance.SetBracketTarget(crew.strID, false);

            _editing = crew;
            FullEditor = true;
            UiSync.SetInteractionOverride(crew, crew, isClient); // окно спросит «с кем взаимодействую» — ответ: с собой

            CrewSim.RaiseUI(GuiKey, crew);

            if (CrewSim.goUI == null)
            {
                // Игра отказалась открывать (соц. режим, предмет на курсоре и т.п.)
                GameLog.Warn("[Look] Не удалось открыть окно внешности — закройте другие окна и нажмите F4");
                Cleanup();
            }
        }

        /// <summary>Окно внешности закрылось (patch на GUIChargenBody.SaveAndClose).</summary>
        internal static void OnEditorClosed(CondOwner? coUser)
        {
            if (coUser != null)
                Submit(coUser);

            Cleanup();
        }

        private static void Cleanup()
        {
            if (_editing != null && _editing.mapGUIPropMaps != null)
                _editing.mapGUIPropMaps.Remove(GuiKey);

            _editing = null;
            FullEditor = false;
            UiSync.ClearInteractionOverride();
        }

        // ===================================================================
        //                              Сеть
        // ===================================================================

        public sealed class Look
        {
            public string CrewId = "";
            public string[] FaceParts = new string[0];
            public string BodyType = "";
            public string[] CondsOn = new string[0];

            public void Write(NetDataWriter w)
            {
                w.Put(Msg);
                w.Put(CrewId);
                w.Put(FaceParts.Length);
                foreach (string s in FaceParts) w.Put(s ?? "");
                w.Put(BodyType ?? "");
                w.Put(CondsOn.Length);
                foreach (string s in CondsOn) w.Put(s);
            }

            public static Look Read(NetPacketReader r)
            {
                var look = new Look { CrewId = r.GetString(128) };

                int n = r.GetInt();
                if (n < 0 || n > 64) n = 0;
                look.FaceParts = new string[n];
                for (int i = 0; i < n; i++) look.FaceParts[i] = r.GetString(128);

                look.BodyType = r.GetString(64);

                int c = r.GetInt();
                if (c < 0 || c > 32) c = 0;
                look.CondsOn = new string[c];
                for (int i = 0; i < c; i++) look.CondsOn[i] = r.GetString(64);

                return look;
            }
        }

        private static Look Capture(CondOwner co)
        {
            var on = new List<string>();
            foreach (string cond in ManagedConds)
            {
                if (co.HasCond(cond))
                    on.Add(cond);
            }

            return new Look
            {
                CrewId = CoLookup.Id(co),
                FaceParts = co.Crew != null && co.Crew.FaceParts != null ? (string[])co.Crew.FaceParts.Clone() : new string[0],
                BodyType = co.Crew != null ? co.Crew.BodyType ?? "" : "",
                CondsOn = on.ToArray()
            };
        }

        /// <summary>Отправить внешность после редактирования.</summary>
        private static void Submit(CondOwner co)
        {
            var net = NetworkManager.Current;
            if (net == null || !net.IsRunning) return; // одиночная игра — менять нечего

            if (net.Role == NetworkManager.NetRole.Client)
            {
                if (!net.IsWorldReady || !PlayerRegistry.IsMine(co)) return; // клиент шлёт только своего
            }
            else if (PlayerRegistry.IsRemoteControlled(co))
            {
                return; // хост не меняет внешность чужого игрока
            }

            var w = new NetDataWriter();
            Capture(co).Write(w);
            net.SendRaw(w, DeliveryMethod.ReliableOrdered); // хост → всем клиентам, клиент → хосту

            GameLog.Info($"[Look] Appearance of {co.strName} sent");
        }

        /// <summary>Хост: внешность от клиента — применить и разослать всем (включая отправителя).</summary>
        public static void ApplyAndRelay(Look look)
        {
            if (!Apply(look)) return;

            var w = new NetDataWriter();
            look.Write(w);
            NetworkManager.Current?.SendRaw(w, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Применить внешность к персонажу.</summary>
        public static bool Apply(Look look)
        {
            CondOwner? co = CoLookup.Find(look.CrewId);
            if (co == null || co.Crew == null)
            {
                GameLog.Warn($"[Look] Character {look.CrewId} not found");
                return false;
            }

            // Сеттеры Crew сами перерисовывают тело и лицо (SetBodyFaceSkin)
            if (look.FaceParts.Length > 0)
                co.Crew.FaceParts = look.FaceParts;
            if (!string.IsNullOrEmpty(look.BodyType))
                co.Crew.BodyType = look.BodyType;

            var on = new HashSet<string>(look.CondsOn);
            foreach (string cond in ManagedConds)
            {
                if (on.Contains(cond))
                {
                    if (!co.HasCond(cond)) co.SetCondAmount(cond, 1.0, 0.0);
                }
                else if (co.HasCond(cond))
                {
                    co.ZeroCondAmount(cond);
                }
            }

            if (co.pspec != null)
            {
                if (on.Contains("IsFemale")) co.pspec.strGender = "IsFemale";
                else if (on.Contains("IsMale")) co.pspec.strGender = "IsMale";
                else if (on.Contains("IsNB")) co.pspec.strGender = "IsNB";
            }

            MonoSingleton<GUIRenderTargets>.Instance.SetFace(co, true); // обновить портрет

            GameLog.Info($"[Look] Appearance of {co.strName} updated");
            return true;
        }

        public static void Reset()
        {
            _openTimer = -1f;
            if (_editing != null) Cleanup();
        }
    }

    // ---------------- Патчи окна GUIChargenBody ----------------

    /// <summary>Включить кнопки, которые игра прячет вне создания персонажа.</summary>
    [HarmonyPatch(typeof(GUIChargenBody), "GetCOInfo")]
    public static class Patch_GUIChargenBody_GetCOInfo
    {
        static void Postfix(GUIChargenBody __instance)
        {
            if (!AppearanceSync.FullEditor) return;

            var t = Traverse.Create(__instance);
            foreach (string field in AppearanceSync.HiddenButtons)
            {
                if (t.Field(field).GetValue() is Component c && c != null)
                    c.gameObject.SetActive(true);
            }
        }
    }

    /// <summary>Окно закрылось — отправить результат.</summary>
    [HarmonyPatch(typeof(GUIChargenBody), nameof(GUIChargenBody.SaveAndClose))]
    public static class Patch_GUIChargenBody_SaveAndClose
    {
        static void Postfix(GUIChargenBody __instance)
        {
            var coUser = Traverse.Create(__instance).Field("coUser").GetValue() as CondOwner;
            AppearanceSync.OnEditorClosed(coUser);
        }
    }

    /// <summary>
    /// Смена имени меняет strID (GUIChargenBody.ChangeName: coUser.strID = value),
    /// а вся синхронизация держится на strID. В мультиплеере имя пока не меняем.
    /// </summary>
    [HarmonyPatch(typeof(GUIChargenBody), "ChangeName")]
    public static class Patch_GUIChargenBody_ChangeName
    {
        static bool Prefix()
        {
            var net = NetworkManager.Current;
            if (net == null || !net.IsRunning) return true;

            GameLog.Warn("[Look] Имя в мультиплеере пока менять нельзя");
            return false;
        }
    }
}
