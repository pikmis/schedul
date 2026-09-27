using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ScheduleIRussian
{
    [BepInPlugin("schedulei.russian.translation", "Schedule I Russian Translation", "1.2.0")]
    public sealed class Plugin : BasePlugin
    {
        internal const string MagicHeader = "# SCHEDULEI_RU_BASE";

        internal static Dictionary<string, string> Translations = new Dictionary<string, string>(StringComparer.Ordinal);
        internal static readonly HashSet<string> Untranslated = new HashSet<string>(StringComparer.Ordinal);
        internal static readonly HashSet<string> Displayed = new HashSet<string>(StringComparer.Ordinal);
        internal static readonly HashSet<string> AllTexts = new HashSet<string>(StringComparer.Ordinal);
        internal static ManualLogSource Logger;
        internal static string UntranslatedPath;
        internal static string DisplayedPath;
        internal static string AllTextsPath;
        internal static TranslationOverlay Overlay;

        internal static ConfigEntry<bool> RemoteEnabled;
        internal static ConfigEntry<string> RemoteUrl;
        internal static ConfigEntry<string> RemoteUrlFallback;
        internal static ConfigEntry<int> RemoteCheckMinutes;
        internal static ConfigEntry<float> AutoScanSeconds;

        private static UnityWebRequest pendingRequest;
        private static AsyncOperation pendingOperation;
        private static bool fetching;
        private static bool usingFallbackUrl;
        private static string lastRemoteText;
        private static Dictionary<string, string> localBase;
        private static bool fetchFailedSignal;
        private static readonly Dictionary<string, string> translateCache = new Dictionary<string, string>(StringComparer.Ordinal);
        private static KeyValuePair<string, string>[] longKeys = Array.Empty<KeyValuePair<string, string>>();
        private static readonly System.Collections.Concurrent.ConcurrentQueue<string> untranslatedQueue = new System.Collections.Concurrent.ConcurrentQueue<string>();
        private static readonly System.Collections.Concurrent.ConcurrentQueue<string> displayedQueue = new System.Collections.Concurrent.ConcurrentQueue<string>();
        private static readonly System.Collections.Concurrent.ConcurrentQueue<string> allTextsQueue = new System.Collections.Concurrent.ConcurrentQueue<string>();
        private static readonly object fileWriteLock = new object();
        private static long hookTicks;
        private static long hookCalls;

        public override void Load()
        {
            Logger = base.Log;

            RemoteEnabled = Config.Bind("Remote", "Enabled", true, "Скачивать свежие переводы с GitHub при запуске");
            RemoteUrl = Config.Bind("Remote", "Url", "https://raw.githubusercontent.com/pikmis/schedulerus/main/TranslaitBROO.txt", "Основной URL базы переводов");
            RemoteUrlFallback = Config.Bind("Remote", "UrlFallback", "https://cdn.jsdelivr.net/gh/pikmis/schedulerus@main/TranslaitBROO.txt", "Зеркало на случай, если основной URL недоступен");
            RemoteCheckMinutes = Config.Bind("Remote", "CheckIntervalMinutes", 60, "Как часто проверять обновления, 0 = только при старте");
            AutoScanSeconds = Config.Bind("Scan", "AutoScanSeconds", 30f, "Периодический авто-скан текстов сцены, 0 = выключить");

            LoadTranslations(Path.Combine(Paths.GameRootPath, "TranslaitBROO.txt"));
            LoadRemoteCache();
            UntranslatedPath = Path.Combine(Paths.GameRootPath, "Untranslated.txt");
            DisplayedPath = Path.Combine(Paths.GameRootPath, "AllDisplayed.txt");
            AllTextsPath = Path.Combine(Paths.GameRootPath, "AllTexts.txt");
            LoadUntranslated();
            LoadDisplayed();
            LoadAllTexts();
            StartFileWriter();
            new Harmony("schedulei.russian.translation").PatchAll();
            Overlay = AddComponent<TranslationOverlay>();
            Logger.LogInfo("Russian translation loaded: " + Translations.Count + " entries (remote " + (RemoteEnabled.Value ? "on" : "off") + ")");
        }

        private static void LoadUntranslated()
        {
            if (!File.Exists(UntranslatedPath))
                return;

            foreach (string line in File.ReadAllLines(UntranslatedPath, new UTF8Encoding(false)))
            {
                int separator = line.IndexOf('=');
                if (separator > 0)
                {
                    string text = line.Substring(0, separator);
                    if (LooksLikeEnglish(text))
                        Untranslated.Add(text);
                }
            }
        }

        private static void LoadDisplayed()
        {
            if (!File.Exists(DisplayedPath))
                return;

            foreach (string line in File.ReadAllLines(DisplayedPath, new UTF8Encoding(false)))
            {
                int separator = line.IndexOf('=');
                if (separator > 0)
                {
                    string text = line.Substring(0, separator);
                    if (LooksLikeEnglish(text))
                        Displayed.Add(text);
                }
            }
        }

        private static void LoadAllTexts()
        {
            if (!File.Exists(AllTextsPath))
                return;

            foreach (string line in File.ReadAllLines(AllTextsPath, new UTF8Encoding(false)))
            {
                int separator = line.IndexOf('=');
                if (separator > 0)
                {
                    string text = line.Substring(0, separator);
                    if (LooksLikeEnglish(text))
                        AllTexts.Add(text);
                }
            }
        }

        internal static void RecordDisplayed(string text)
        {
            string normalized = NormalizeLineBreaks(text);
            if (!LooksLikeEnglish(text) || !Displayed.Add(normalized))
                return;

            displayedQueue.Enqueue(normalized + "=" + Environment.NewLine);

            RecordAllText(text);
        }

        internal static void RecordAllText(string text)
        {
            string normalized = NormalizeLineBreaks(text);
            if (!LooksLikeEnglish(text) || !AllTexts.Add(normalized))
                return;

            allTextsQueue.Enqueue(normalized + "=" + Environment.NewLine);
        }

        internal static void RecordUntranslated(string text)
        {
            string normalized = NormalizeLineBreaks(text);
            if (!LooksLikeEnglish(text) || TryTranslate(text, out _) || !Untranslated.Add(normalized))
                return;

            WriteUntranslated(normalized);
        }

        internal static void RecordUntranslatedUnchecked(string text)
        {
            string normalized = NormalizeLineBreaks(text);
            if (!LooksLikeEnglish(text) || !Untranslated.Add(normalized))
                return;

            WriteUntranslated(normalized);
        }

        private static void WriteUntranslated(string normalized)
        {
            untranslatedQueue.Enqueue(normalized + "=" + Environment.NewLine);
        }

        private static void StartFileWriter()
        {
            System.Threading.Thread thread = new System.Threading.Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        DrainQueue(untranslatedQueue, UntranslatedPath);
                        DrainQueue(displayedQueue, DisplayedPath);
                        DrainQueue(allTextsQueue, AllTextsPath);
                        System.Threading.Thread.Sleep(300);
                    }
                    catch (Exception error)
                    {
                        Logger.LogWarning("File writer failed: " + error.Message);
                    }
                }
            });
            thread.IsBackground = true;
            thread.Name = "ScheduleIRussianFileWriter";
            thread.Start();
        }

        internal static TMP_Text[] CollectTmpTexts()
        {
            return Resources.FindObjectsOfTypeAll<TMP_Text>();
        }

        internal static Text[] CollectLegacyTexts()
        {
            return Resources.FindObjectsOfTypeAll<Text>();
        }

        private static GameObject ddolScanAnchor;

        internal static List<GameObject> CollectScanRoots()
        {
            List<GameObject> roots = new List<GameObject>(256);
            for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                Scene scene = SceneManager.GetSceneAt(sceneIndex);
                if (scene.isLoaded)
                    roots.AddRange(scene.GetRootGameObjects());
            }

            if (ddolScanAnchor == null)
            {
                ddolScanAnchor = new GameObject("ScheduleIRussian.ScanAnchor");
                UnityEngine.Object.DontDestroyOnLoad(ddolScanAnchor);
            }

            roots.AddRange(ddolScanAnchor.scene.GetRootGameObjects());
            return roots;
        }

        internal static object FileWriteLock => fileWriteLock;

        internal static void DrainAllWriteQueues()
        {
            while (untranslatedQueue.TryDequeue(out _))
            {
            }

            while (displayedQueue.TryDequeue(out _))
            {
            }

            while (allTextsQueue.TryDequeue(out _))
            {
            }
        }

        private static void DrainQueue(System.Collections.Concurrent.ConcurrentQueue<string> queue, string path)
        {
            if (queue.IsEmpty)
                return;

            StringBuilder batch = new StringBuilder();
            while (queue.TryDequeue(out string line))
                batch.Append(line);

            if (batch.Length == 0)
                return;

            lock (fileWriteLock)
            {
                File.AppendAllText(path, batch.ToString(), new UTF8Encoding(false));
            }
        }

        private static bool LooksLikeEnglish(string text)
        {
            bool hasLatinLetter = false;
            foreach (char character in text)
            {
                if ((character >= 'А' && character <= 'я') || character == 'Ё' || character == 'ё')
                    return false;

                if ((character >= 'A' && character <= 'Z') || (character >= 'a' && character <= 'z'))
                    hasLatinLetter = true;
            }

            if (!hasLatinLetter)
                return false;

            if (text.Length > 0 && text.Length <= 16 && text[0] >= '0' && text[0] <= '9' && IsNumericUnit(text))
                return false;

            return true;
        }

        private static bool IsNumericUnit(string text)
        {
            int index = 0;
            while (index < text.Length && ((text[index] >= '0' && text[index] <= '9') || text[index] == '.' || text[index] == ','))
                index++;

            if (index == 0)
                return false;

            while (index < text.Length && (text[index] == ' ' || text[index] == '\t'))
                index++;

            int rest = text.Length - index;
            if (rest < 1 || rest > 5)
                return false;

            for (; index < text.Length; index++)
            {
                char character = text[index];
                bool unitCharacter = (character >= 'A' && character <= 'Z') || (character >= 'a' && character <= 'z') || character == '°' || character == '%';
                if (!unitCharacter)
                    return false;
            }

            return true;
        }

        internal static bool TryTranslate(string source, out string translated)
        {
            string normalized = NormalizeLineBreaks(source);
            if (translateCache.TryGetValue(normalized, out translated))
                return translated != null;

            if (translateCache.Count > 20000)
                translateCache.Clear();

            bool success = TryTranslateCore(normalized, out translated);
            translateCache[normalized] = success ? translated : null;
            return success;
        }

        private static bool TryTranslateCore(string normalized, out string translated)
        {
            if (normalized == "Talk to 'U.N.' at a payphone")
            {
                translated = "Поговорите с дядей Нельсоном у таксофона";
                return true;
            }

            if (normalized.Contains("\\n- U.N.", StringComparison.Ordinal))
            {
                translated = "Слыхал, ты влип. По мобиле такие дела не обсуждают — слишком много ушей. Найди таксофон.\n- Дядя Нельсон";
                return true;
            }

            if (Translations.TryGetValue(normalized, out translated))
                return true;

            Match pourProgress = Regex.Match(normalized, @"^Pour into pot \((\d+)%\)$");
            if (pourProgress.Success)
            {
                translated = "Залить в горшок (" + pourProgress.Groups[1].Value + "%)";
                return true;
            }

            Match tapProgress = Regex.Match(normalized, @"^Click and hold tap to fill \((\d+)%\)$");
            if (tapProgress.Success)
            {
                translated = "Нажмите и удерживайте кран, чтобы наполнить (" + tapProgress.Groups[1].Value + "%)";
                return true;
            }

            Match buryProgress = Regex.Match(normalized, @"^Click soil chunks to bury seed \((\d+)/6\)$");
            if (buryProgress.Success)
            {
                translated = "Нажмите на комки почвы, чтобы закопать семя (" + buryProgress.Groups[1].Value + "/6)";
                return true;
            }

            Match deadDropWait = Regex.Match(normalized, @"^Wait for the dead drop \((\d+) mins?\)$");
            if (deadDropWait.Success)
            {
                translated = "Дождитесь тайника (" + deadDropWait.Groups[1].Value + " мин.)";
                return true;
            }

            Match clock = Regex.Match(normalized, @"^(\d{1,2}):(\d{2}) (AM|PM)( (Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday))?$");
            if (clock.Success)
            {
                int hour = int.Parse(clock.Groups[1].Value);
                string marker = clock.Groups[3].Value;
                if (marker == "PM" && hour < 12)
                    hour += 12;
                else if (marker == "AM" && hour == 12)
                    hour = 0;

                string time = hour.ToString("00") + ":" + clock.Groups[2].Value;
                translated = (clock.Groups[5].Success ? WeekdayRussian(clock.Groups[5].Value) + " " : "") + time;
                return true;
            }

            Match daysAgo = Regex.Match(normalized, @"^(\d+) days? ago$");
            if (daysAgo.Success)
            {
                translated = PluralDays(int.Parse(daysAgo.Groups[1].Value)) + " назад";
                return true;
            }

            foreach (KeyValuePair<string, string> pair in longKeys)
            {
                if (normalized.StartsWith(pair.Key, StringComparison.Ordinal))
                {
                    translated = pair.Value + normalized.Substring(pair.Key.Length);
                    return true;
                }
            }

            return false;
        }

        private static string WeekdayRussian(string weekday)
        {
            switch (weekday)
            {
                case "Monday": return "Понедельник";
                case "Tuesday": return "Вторник";
                case "Wednesday": return "Среда";
                case "Thursday": return "Четверг";
                case "Friday": return "Пятница";
                case "Saturday": return "Суббота";
                case "Sunday": return "Воскресенье";
                default: return weekday;
            }
        }

        private static string PluralDays(int count)
        {
            int lastTwo = count % 100;
            int last = count % 10;
            if (lastTwo >= 11 && lastTwo <= 14)
                return count + " дней";
            if (last == 1)
                return count + " день";
            if (last >= 2 && last <= 4)
                return count + " дня";
            return count + " дней";
        }

        private static string NormalizeLineBreaks(string value)
        {
            if (value.IndexOf('\r') < 0 && value.IndexOf('\n') < 0)
                return value;

            return value.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\\n");
        }

        private static void LoadTranslations(string path)
        {
            if (!File.Exists(path))
            {
                Logger.LogError("Translation file not found: " + path);
                return;
            }

            Dictionary<string, string> parsed = ParseTranslationLines(File.ReadAllLines(path, new UTF8Encoding(false)));
            foreach (KeyValuePair<string, string> pair in parsed)
                Translations[pair.Key] = pair.Value;

            localBase = new Dictionary<string, string>(Translations);
            RefreshDerived();
        }

        internal static Dictionary<string, string> ParseTranslationLines(IEnumerable<string> lines)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string rawLine in lines)
            {
                if (string.IsNullOrWhiteSpace(rawLine) || rawLine[0] == '#')
                    continue;

                int separator = FindTranslationSeparator(rawLine);
                if (separator <= 0 || separator == rawLine.Length - 1)
                    continue;

                string source = rawLine.Substring(0, separator);
                string translated = rawLine.Substring(separator + 1).Replace("\\n", "\n");
                result[NormalizeLineBreaks(source)] = translated;
            }

            return result;
        }

        private static bool HasMagicHeader(IEnumerable<string> lines)
        {
            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                return string.Equals(line.Trim(), MagicHeader, StringComparison.Ordinal);
            }

            return false;
        }

        private static string RemoteCachePath
        {
            get { return Path.Combine(Paths.ConfigPath, "TranslaitBROO.remote.txt"); }
        }

        private static void LoadRemoteCache()
        {
            try
            {
                if (!File.Exists(RemoteCachePath))
                    return;

                string[] lines = File.ReadAllLines(RemoteCachePath, new UTF8Encoding(false));
                if (!HasMagicHeader(lines))
                {
                    Logger.LogWarning("Remote cache rejected: missing " + MagicHeader);
                    return;
                }

                Dictionary<string, string> parsed = ParseTranslationLines(lines);
                if (parsed.Count == 0)
                    return;

            Translations = MergeDictionaries(parsed);
            RefreshDerived();
                RefreshDerived();
                lastRemoteText = File.ReadAllText(RemoteCachePath, new UTF8Encoding(false));
                Logger.LogInfo("Remote translation cache applied: " + parsed.Count + " entries");
            }
            catch (Exception error)
            {
                Logger.LogWarning("Remote cache load failed: " + error.Message);
            }
        }

        private static Dictionary<string, string> MergeDictionaries(Dictionary<string, string> overlay)
        {
            Dictionary<string, string> merged = localBase != null
                ? new Dictionary<string, string>(localBase, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (KeyValuePair<string, string> pair in overlay)
                merged[pair.Key] = pair.Value;

            return merged;
        }

        internal static bool IsFetching
        {
            get { return fetching; }
        }

        internal static bool ConsumeFetchFailure()
        {
            bool failed = fetchFailedSignal;
            fetchFailedSignal = false;
            return failed;
        }

        internal static void StartFetch()
        {
            if (!RemoteEnabled.Value || fetching)
                return;

            usingFallbackUrl = false;
            BeginRequest(RemoteUrl.Value);
        }

        private static void BeginRequest(string url)
        {
            try
            {
                UnityWebRequest request = UnityWebRequest.Get(url);
                request.timeout = 120;
                pendingRequest = request;
                pendingOperation = request.SendWebRequest();
                fetching = true;
            }
            catch (Exception error)
            {
                fetching = false;
                fetchFailedSignal = true;
                Logger.LogWarning("Remote fetch failed to start: " + error.Message);
            }
        }

        internal static void PollFetch()
        {
            if (!fetching || pendingOperation == null || !pendingOperation.isDone)
                return;

            UnityWebRequest request = pendingRequest;
            fetching = false;
            pendingRequest = null;
            pendingOperation = null;

            try
            {
                if (request.result != UnityWebRequest.Result.Success)
                {
                    if (!usingFallbackUrl && !string.IsNullOrEmpty(RemoteUrlFallback.Value))
                    {
                        usingFallbackUrl = true;
                        Logger.LogWarning("Remote fetch failed (" + request.error + "), trying fallback");
                        BeginRequest(RemoteUrlFallback.Value);
                        return;
                    }

                    fetchFailedSignal = true;
                    Logger.LogWarning("Remote fetch failed: " + request.error);
                    return;
                }

                ApplyRemoteText(request.downloadHandler.text);
            }
            catch (Exception error)
            {
                Logger.LogWarning("Remote fetch handling failed: " + error.Message);
            }
            finally
            {
                request.Dispose();
            }
        }

        private static void ApplyRemoteText(string text)
        {
            if (string.IsNullOrEmpty(text) || text == lastRemoteText)
                return;

            string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            if (!HasMagicHeader(lines))
            {
                Logger.LogWarning("Remote file rejected: missing " + MagicHeader);
                return;
            }

            Dictionary<string, string> parsed = ParseTranslationLines(lines);
            if (parsed.Count == 0)
            {
                Logger.LogWarning("Remote file rejected: no translations parsed");
                return;
            }

            if (Translations.Count > 0 && parsed.Count < Translations.Count / 2)
            {
                Logger.LogWarning("Remote file rejected: too small (" + parsed.Count + " vs " + Translations.Count + ")");
                return;
            }

            Translations = MergeDictionaries(parsed);

            try
            {
                File.WriteAllText(RemoteCachePath, text, new UTF8Encoding(false));
            }
            catch (Exception error)
            {
                Logger.LogWarning("Could not write remote cache: " + error.Message);
            }

            lastRemoteText = text;
            Logger.LogInfo("Remote translations applied: " + parsed.Count + " entries (total " + Translations.Count + ")");
            if (Overlay != null)
                Overlay.ScanScene(true);
        }

        internal static void TranslateInPlace(ref string value)
        {
            long hookStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                TranslateInPlaceCore(ref value);
            }
            finally
            {
                hookTicks += System.Diagnostics.Stopwatch.GetTimestamp() - hookStart;
                hookCalls++;
            }
        }

        internal static long ConsumeHookTicks()
        {
            long value = hookTicks;
            hookTicks = 0;
            return value;
        }

        internal static long ConsumeHookCalls()
        {
            long value = hookCalls;
            hookCalls = 0;
            return value;
        }

        private static void TranslateInPlaceCore(ref string value)
        {
            if (string.IsNullOrEmpty(value))
                return;

            RecordDisplayed(value);

            string translated;
            if (TryTranslate(value, out translated) && value != translated)
                value = translated;
            else
                RecordUntranslatedUnchecked(value);
        }

        private static void RefreshDerived()
        {
            translateCache.Clear();
            longKeys = Translations
                .Where(pair => pair.Key.Length >= 24)
                .ToArray();
        }

        private static int FindTranslationSeparator(string line)
        {
            int tagDepth = 0;
            int lastEqualsBeforeTranslation = -1;
            for (int index = 0; index < line.Length; index++)
            {
                if (line[index] == '<')
                {
                    tagDepth++;
                    continue;
                }

                if (line[index] == '>' && tagDepth > 0)
                {
                    tagDepth--;
                    continue;
                }

                char character = line[index];
                if ((character >= 'А' && character <= 'я') || character == 'Ё' || character == 'ё')
                    return lastEqualsBeforeTranslation;

                if (character == '=' && tagDepth == 0)
                {
                    lastEqualsBeforeTranslation = index;
                }
            }

            return lastEqualsBeforeTranslation;
        }
    }

    public sealed class TranslationOverlay : MonoBehaviour
    {
        private float nextAutoScan;
        private float nextRemoteCheck;
        private float nextPerfReport;
        private int scanHalf;
        private int lastSceneHandle = int.MinValue;
        private int lastSceneCount = int.MinValue;
        private float sceneScanAt1 = -1f;
        private float sceneScanAt2 = -1f;

        public TranslationOverlay(IntPtr ptr) : base(ptr) { }

        private void Update()
        {
            UnityEngine.InputSystem.Keyboard keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null)
                return;

            if (nextRemoteCheck == 0f)
                nextRemoteCheck = Time.time + 15f;

            if (Plugin.IsFetching)
                Plugin.PollFetch();

            if (Plugin.ConsumeFetchFailure())
                nextRemoteCheck = Time.time + 120f;

            if (Plugin.RemoteEnabled.Value && !Plugin.IsFetching && Time.time >= nextRemoteCheck)
            {
                Plugin.StartFetch();
                nextRemoteCheck = Plugin.RemoteCheckMinutes.Value > 0
                    ? Time.time + Plugin.RemoteCheckMinutes.Value * 60f
                    : float.MaxValue;
            }

            if (keyboard.f10Key.wasPressedThisFrame)
            {
                FullScanScene();
                Plugin.Logger.LogInfo("Принудительное обновление перевода: " + Plugin.AllTexts.Count + " строк");
            }

            int sceneHandle = SceneManager.GetActiveScene().handle;
            int sceneCount = SceneManager.sceneCount;
            if (sceneHandle != lastSceneHandle || sceneCount != lastSceneCount)
            {
                lastSceneHandle = sceneHandle;
                lastSceneCount = sceneCount;
                sceneScanAt1 = Time.time + 5f;
                sceneScanAt2 = Time.time + 25f;
            }

            if (sceneScanAt1 > 0f && Time.time >= sceneScanAt1)
            {
                sceneScanAt1 = -1f;
                ScanScene(true, true);
            }

            if (sceneScanAt2 > 0f && Time.time >= sceneScanAt2)
            {
                sceneScanAt2 = -1f;
                ScanScene(true, true);
            }

            if (Plugin.AutoScanSeconds.Value > 0f && Time.time >= nextAutoScan)
            {
                nextAutoScan = Time.time + Plugin.AutoScanSeconds.Value;
                ScanScene(true, (scanHalf++ & 1) == 0);
            }

            if (Time.time >= nextPerfReport)
            {
                nextPerfReport = Time.time + 5f;
                long ticks = Plugin.ConsumeHookTicks();
                long calls = Plugin.ConsumeHookCalls();
                double milliseconds = ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (milliseconds > 5.0)
                    Plugin.Logger.LogWarning("Hooks: " + milliseconds.ToString("F1") + "ms over " + calls + " calls in last 5s");
            }
        }

        internal void ScanScene()
        {
            FullScanScene();
        }

        internal void FullScanScene()
        {
            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
            int tmpCount = 0;
            int legacyCount = 0;
            try
            {
                TMP_Text[] tmpTexts = Plugin.CollectTmpTexts();
                tmpCount = tmpTexts.Length;
                for (int index = 0; index < tmpTexts.Length; index++)
                {
                    TMP_Text text = tmpTexts[index];
                    if (text == null)
                        continue;

                    CaptureAndTranslate(text);
                }

                Text[] legacyTexts = Plugin.CollectLegacyTexts();
                legacyCount = legacyTexts.Length;
                for (int index = 0; index < legacyTexts.Length; index++)
                {
                    Text text = legacyTexts[index];
                    if (text == null)
                        continue;

                    CaptureAndTranslate(text);
                }
            }
            catch (Exception error)
            {
                Plugin.Logger.LogWarning("Full scan failed: " + error.Message);
            }
            finally
            {
                timer.Stop();
                Plugin.Logger.LogInfo("Full scan: " + timer.ElapsedMilliseconds + "ms tmp=" + tmpCount + " legacy=" + legacyCount);
            }
        }

        internal void ScanScene(bool onlyActive)
        {
            ScanScene(onlyActive, true);
        }

        internal void ScanScene(bool onlyActive, bool includeLegacy)
        {
            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
            long collectTicks = 0;
            int tmpTotal = 0;
            int tmpActive = 0;
            int legacyTotal = 0;
            int legacyActive = 0;
            try
            {
                List<GameObject> roots = Plugin.CollectScanRoots();
                for (int rootIndex = 0; rootIndex < roots.Count; rootIndex++)
                {
                    GameObject root = roots[rootIndex];
                    if (root == null)
                        continue;

                    long phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    TMP_Text[] tmpTexts = root.GetComponentsInChildren<TMP_Text>(true);
                    collectTicks += System.Diagnostics.Stopwatch.GetTimestamp() - phaseStart;
                    tmpTotal += tmpTexts.Length;
                    for (int index = 0; index < tmpTexts.Length; index++)
                    {
                        TMP_Text text = tmpTexts[index];
                        if (text == null || (onlyActive && !text.isActiveAndEnabled))
                            continue;

                        tmpActive++;
                        CaptureAndTranslate(text);
                    }

                    if (includeLegacy)
                    {
                        phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
                        Text[] legacyTexts = root.GetComponentsInChildren<Text>(true);
                        collectTicks += System.Diagnostics.Stopwatch.GetTimestamp() - phaseStart;
                        legacyTotal += legacyTexts.Length;
                        for (int index = 0; index < legacyTexts.Length; index++)
                        {
                            Text text = legacyTexts[index];
                            if (text == null || (onlyActive && !text.isActiveAndEnabled))
                                continue;

                            legacyActive++;
                            CaptureAndTranslate(text);
                        }
                    }
                }
            }
            catch (Exception error)
            {
                Plugin.Logger.LogWarning("Scene scan failed: " + error.Message);
            }
            finally
            {
                timer.Stop();
                long processTicks = timer.ElapsedTicks - collectTicks;
                double collectMs = collectTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                double processMs = processTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Plugin.Logger.LogInfo(
                    "Scan: total=" + timer.ElapsedMilliseconds + "ms collect=" + collectMs.ToString("F1") +
                    "ms process=" + processMs.ToString("F1") + "ms tmp=" + tmpActive + "/" + tmpTotal +
                    " legacy=" + legacyActive + "/" + legacyTotal + " (active=" + onlyActive + ")");
                if (timer.ElapsedMilliseconds > 60)
                    Plugin.Logger.LogWarning("Slow scan: " + timer.ElapsedMilliseconds + "ms (onlyActive=" + onlyActive + ", includeLegacy=" + includeLegacy + ")");
            }
        }

        private static void Capture(string value)
        {
            if (string.IsNullOrEmpty(value))
                return;

            Plugin.RecordDisplayed(value);
            if (!Plugin.TryTranslate(value, out _))
                Plugin.RecordUntranslatedUnchecked(value);
        }

        private static void CaptureAndTranslate(TMP_Text text)
        {
            string value = text.text;
            if (string.IsNullOrEmpty(value))
                return;

            Capture(value);
            string translated;
            if (Plugin.TryTranslate(value, out translated) && value != translated)
                text.text = translated;
        }

        private static void CaptureAndTranslate(Text text)
        {
            string value = text.text;
            if (string.IsNullOrEmpty(value))
                return;

            Capture(value);
            string translated;
            if (Plugin.TryTranslate(value, out translated) && value != translated)
                text.text = translated;
        }
    }

    [HarmonyPatch(typeof(TMP_Text), "set_text")]
    internal static class TMPTextPatch
    {
        private static void Prefix(ref string value)
        {
            Plugin.TranslateInPlace(ref value);
        }
    }

    [HarmonyPatch(typeof(Text), "set_text")]
    internal static class UnityTextPatch
    {
        private static void Prefix(ref string value)
        {
            Plugin.TranslateInPlace(ref value);
        }
    }

    [HarmonyPatch(typeof(TMP_Text), "SetText", new Type[] { typeof(string) })]
    internal static class TMPSetTextStringPatch
    {
        private static void Prefix(ref string __0)
        {
            Plugin.TranslateInPlace(ref __0);
        }
    }

    [HarmonyPatch(typeof(TMP_Text), "SetText", new Type[] { typeof(string), typeof(float) })]
    internal static class TMPSetTextFloatPatch
    {
        private static void Prefix(ref string __0)
        {
            Plugin.TranslateInPlace(ref __0);
        }
    }

    [HarmonyPatch(typeof(TMP_Text), "SetText", new Type[] { typeof(string), typeof(float), typeof(int) })]
    internal static class TMPSetTextFloatIntPatch
    {
        private static void Prefix(ref string __0)
        {
            Plugin.TranslateInPlace(ref __0);
        }
    }

    [HarmonyPatch(typeof(TMP_Text), "SetText", new Type[] { typeof(string), typeof(float), typeof(int), typeof(int) })]
    internal static class TMPSetTextFloatIntIntPatch
    {
        private static void Prefix(ref string __0)
        {
            Plugin.TranslateInPlace(ref __0);
        }
    }
}
