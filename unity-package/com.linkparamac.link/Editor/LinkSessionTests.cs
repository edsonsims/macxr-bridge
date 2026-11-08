// SPDX-License-Identifier: MIT
// Link para Mac: sessões gravadas viram testes automáticos da percepção do app.
//   session baseline [nome]   repete a sessão e grava o que aconteceu como esperado (expect.json na pasta dela)
//   session test [nome|all]   repete e compara: classes que o YOLO tem que ver, onde as etiquetas têm que pousar
//                             (tolerância em metros) e classes proibidas. Relatório + JUnit em TestResults/.
// Determinístico: a repetição anda em passo travado com o YOLO — a cada 6 quadros (~5 por segundo de sessão) o teste
// carrega o quadro, libera UMA detecção do app (LinkHooks), espera a inferência terminar e só então avança. O resultado
// não depende da velocidade do Mac. "realtime" no fim do comando desliga (repete em tempo real).
// Linha de comando (abre o Editor, roda todas as sessões com expect.json e fecha com o código de saída):
//   Unity -projectPath <projeto> -executeMethod LinkParaMac.EditorTools.LinkSessionTests.RunAllFromCommandLine
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Meta.XR.BuildingBlocks.AIBlocks;
using UnityEditor;
using UnityEngine;

namespace LinkParaMac.EditorTools
{
    [InitializeOnLoad]
    public static class LinkSessionTests
    {
        [Serializable] public class DetectionExpectation { public string label; public int minCount; }
        [Serializable] public class PlacementExpectation { public string label; public Vector3 position; public float tolerance = 0.15f; }
        [Serializable] public class Expectations
        {
            public List<DetectionExpectation> detections = new List<DetectionExpectation>();
            public List<PlacementExpectation> placed = new List<PlacementExpectation>();
            public List<string> forbidden = new List<string>();
        }

        class Result
        {
            public string session, dir;
            public readonly List<(string name, bool ok, string message)> checks = new List<(string, bool, string)>();
            public double seconds;
            public bool Passed => checks.All(c => c.ok);
        }

        const string CliKey = "LinkParaMac.SessionTests.CommandLine";
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly Queue<string> _queue = new Queue<string>();
        static readonly List<Result> _results = new List<Result>();
        static bool _running, _baseline, _waitingStart, _lockstep = true, _advancePending;
        static double _watchdog;
        const int StepFrames = 6;
        static bool _wasBusy, _endedByTest;
        enum Phase { Load, WaitStart, WaitDone }
        static Phase _phase;
        static double _busySince;
        static int _inferences, _steps;
        static double _inferenceSeconds;
        static readonly System.Reflection.FieldInfo BusyField =
            typeof(ObjectDetectionAgent).GetField("_busy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        static string _current, _currentDir;
        static double _startedAt, _cliStartAt;
        static readonly Dictionary<string, int> _detections = new Dictionary<string, int>();
        static readonly Dictionary<string, List<Vector3>> _placed = new Dictionary<string, List<Vector3>>();
        static ObjectDetectionAgent _agent;
        public static string LastReport { get; private set; } = "-";
        public static string Progress => _running
            ? $"{_current}: quadro {LinkSession.CameraFrameIndex + 1}/{LinkSession.CameraFrameCount}, {_inferences} inferências " +
              $"(média {(_inferences > 0 ? _inferenceSeconds / _inferences * 1000 : 0):0} ms), {_steps} passos"
            : "parado";
        public static bool Running => _running;

        static LinkSessionTests()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            LinkHooks.Placed -= OnPlaced;
            LinkHooks.Placed += OnPlaced;
            EditorApplication.playModeStateChanged += s =>
            {
                if (s == PlayModeStateChange.EnteredPlayMode && EditorPrefs.GetBool(CliKey, false)) _cliStartAt = EditorApplication.timeSinceStartup + 8;
                if (s == PlayModeStateChange.ExitingPlayMode) { _running = false; _queue.Clear(); _agent = null; }
            };
        }

        static string Root => Path.Combine(Directory.GetCurrentDirectory(), "Sessions");

        public static string Command(string verb, string arg)
        {
            if (!EditorApplication.isPlaying) return "só em Play Mode";
            if (_running) return "testes já rodando: " + _current;
            _results.Clear();
            _queue.Clear();
            arg = (arg ?? "").Trim();
            _lockstep = !arg.Contains("realtime");
            arg = arg.Replace("realtime", "").Trim();
            if (verb == "baseline")
            {
                _baseline = true;
                _queue.Enqueue(arg);
                return "gravando o esperado de " + (arg == "" ? "a última sessão" : arg) + " (a sessão vai repetir inteira)";
            }
            _baseline = false;
            if (arg == "" || arg == "all")
            {
                if (Directory.Exists(Root))
                    foreach (var d in new DirectoryInfo(Root).GetDirectories().OrderBy(d => d.Name))
                        if (File.Exists(Path.Combine(d.FullName, "expect.json"))) _queue.Enqueue(d.Name);
                if (_queue.Count == 0) return "nenhuma sessão com expect.json (use session baseline <nome> antes)";
            }
            else _queue.Enqueue(arg);
            return $"rodando {_queue.Count} teste(s) de sessão: {string.Join(", ", _queue)}";
        }

        public static void RunAllFromCommandLine()
        {
            EditorPrefs.SetBool(CliKey, true);
            EditorApplication.EnterPlaymode();
        }

        static void OnPlaced(string label, Vector3 position)
        {
            if (!_running || _waitingStart) return;
            if (!_placed.TryGetValue(label, out var list)) _placed[label] = list = new List<Vector3>();
            list.Add(position);
        }

        static void OnBoxes(List<BoxData> boxes)
        {
            if (!_running || _waitingStart) return;
            foreach (var b in boxes)
            {
                string cls = b.label ?? "?";
                string name = cls.Contains(' ') ? cls.Substring(0, cls.LastIndexOf(' ')) : cls;
                _detections[name] = _detections.TryGetValue(name, out var n) ? n + 1 : 1;
            }
        }

        static void Tick()
        {
            if (!EditorApplication.isPlaying) return;
            if (_cliStartAt > 0 && EditorApplication.timeSinceStartup >= _cliStartAt)
            {
                _cliStartAt = 0;
                Debug.Log("[LinkSessionTests] linha de comando: " + Command("test", "all"));
                if (_queue.Count == 0) Finish();
            }
            if (_agent == null)
            {
                _agent = UnityEngine.Object.FindAnyObjectByType<ObjectDetectionAgent>(FindObjectsInactive.Include);
                if (_agent != null) _agent.OnDetectionResponseReceived.AddListener(OnBoxes);
            }
            if (!_running)
            {
                if (_queue.Count == 0) return;
                StartNext();
                return;
            }
            if (_waitingStart)
            {
                if (LinkSession.Replaying && LinkSession.CurrentTime > 0)
                {
                    _waitingStart = false;
                    if (_lockstep)
                    {
                        LinkHooks.HoldDetection?.Invoke();
                        LinkSession.Seek(0);
                        LinkSession.Pause(true);
                        _phase = Phase.WaitDone; // espera a que estiver rodando terminar, depois o primeiro passo
                        _busySince = EditorApplication.timeSinceStartup;
                        _watchdog = EditorApplication.timeSinceStartup + 8;
                    }
                }
                else if (EditorApplication.timeSinceStartup - _startedAt > 30) { Fail("a repetição não começou"); }
                return;
            }
            if (!LinkSession.Replaying) { Evaluate(); return; }
            if (!_lockstep) return;
            double now = EditorApplication.timeSinceStartup;
            bool busy = _agent != null && BusyField != null && (bool)BusyField.GetValue(_agent);
            bool hooks = LinkHooks.DetectNow != null && LinkHooks.HoldDetection != null && LinkHooks.DetectionStarted != null;
            switch (_phase)
            {
                case Phase.Load: // o quadro do passo já foi carregado (um tick depois do Step): libera uma detecção
                    if (!hooks) { if (now > _watchdog) Advance(now, 0.4); return; }
                    if (busy) return; // a anterior ainda termina
                    LinkHooks.DetectNow();
                    _phase = Phase.WaitStart;
                    _watchdog = now + 3;
                    return;
                case Phase.WaitStart:
                    if (LinkHooks.DetectionStarted())
                    {
                        LinkHooks.HoldDetection(); // só uma por quadro
                        _busySince = now;
                        _phase = Phase.WaitDone;
                        _watchdog = now + 8;
                    }
                    else if (now > _watchdog) Advance(now, 0);
                    return;
                case Phase.WaitDone:
                    if (busy && now < _watchdog) return;
                    _inferences++;
                    _inferenceSeconds += now - _busySince;
                    Advance(now, 0);
                    return;
            }
        }

        // Próximo passo (as etiquetas da resposta anterior já foram tratadas com a pose daquele quadro).
        static void Advance(double now, double wait)
        {
            if (LinkSession.CameraFrameIndex >= LinkSession.CameraFrameCount - 1) { _endedByTest = true; LinkSession.Command("stop"); return; }
            LinkSession.Step(StepFrames);
            _steps++;
            _phase = Phase.Load;
            _watchdog = now + wait;
        }

        static void StartNext()
        {
            _current = _queue.Dequeue();
            _detections.Clear();
            _placed.Clear();
            if (LinkSession.Replaying) LinkSession.Command("stop");
            string msg = LinkSession.Command("play " + _current);
            if (!msg.StartsWith("reproduzindo"))
            {
                _results.Add(new Result { session = _current, checks = { ("repetição", false, msg) } });
                Debug.LogWarning("[LinkSessionTests] " + msg);
                if (_queue.Count == 0) Finish();
                return;
            }
            _currentDir = Path.Combine(Root, LinkSession.ReplayName);
            _running = true;
            _endedByTest = false;
            _inferences = _steps = 0;
            _inferenceSeconds = 0;
            _waitingStart = true;
            _startedAt = EditorApplication.timeSinceStartup;
            Debug.Log("[LinkSessionTests] " + msg);
        }

        static void Fail(string why)
        {
            _results.Add(new Result { session = _current, dir = _currentDir, checks = { ("repetição", false, why) } });
            _running = false;
            if (_queue.Count == 0) Finish();
        }

        static Vector3 Median(List<Vector3> points)
        {
            float M(IEnumerable<float> v) { var s = v.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
            return new Vector3(M(points.Select(p => p.x)), M(points.Select(p => p.y)), M(points.Select(p => p.z)));
        }

        static void Evaluate()
        {
            _running = false;
            var result = new Result { session = Path.GetFileName(_currentDir), dir = _currentDir, seconds = EditorApplication.timeSinceStartup - _startedAt };
            string expectPath = Path.Combine(_currentDir, "expect.json");
            bool complete = _lockstep ? _endedByTest : LinkSession.LastStopReason == "fim";
            Debug.Log($"[LinkSessionTests] {_inferences} inferências (média {(_inferences > 0 ? _inferenceSeconds / _inferences * 1000 : 0):0} ms), {_steps} passos");
            if (!complete)
                result.checks.Add(("repetição", false, "interrompida antes do fim (" + LinkSession.LastStopReason + ")" + (_baseline ? " — expect.json não foi alterado" : "")));
            else if (_baseline)
            {
                var e = new Expectations();
                foreach (var kv in _detections.Where(kv => kv.Value >= 3).OrderByDescending(kv => kv.Value))
                    e.detections.Add(new DetectionExpectation { label = kv.Key, minCount = Math.Max(1, kv.Value / 2) });
                foreach (var kv in _placed.Where(kv => kv.Value.Count >= 3))
                    e.placed.Add(new PlacementExpectation { label = kv.Key, position = Median(kv.Value), tolerance = 0.15f });
                File.WriteAllText(expectPath, JsonUtility.ToJson(e, true));
                result.checks.Add(("baseline", true, $"expect.json com {e.detections.Count} classes e {e.placed.Count} posições"));
            }
            else if (File.Exists(expectPath))
            {
                var e = JsonUtility.FromJson<Expectations>(File.ReadAllText(expectPath));
                foreach (var d in e.detections)
                {
                    int n = _detections.TryGetValue(d.label, out var c) ? c : 0;
                    result.checks.Add(($"detecta {d.label}", n >= d.minCount, $"{n} caixas (mínimo {d.minCount})"));
                }
                foreach (var p in e.placed)
                {
                    if (!_placed.TryGetValue(p.label, out var list) || list.Count == 0)
                    {
                        result.checks.Add(($"etiqueta {p.label}", false, "não pousou"));
                        continue;
                    }
                    float dist = Vector3.Distance(Median(list), p.position);
                    result.checks.Add(($"etiqueta {p.label}", dist <= p.tolerance, $"a {dist * 100f:0} cm do esperado (tolerância {p.tolerance * 100f:0} cm)"));
                }
                foreach (var f in e.forbidden)
                {
                    int n = _detections.TryGetValue(f, out var c) ? c : 0;
                    result.checks.Add(($"não detecta {f}", n == 0, n == 0 ? "ok" : $"{n} caixas"));
                }
            }
            else result.checks.Add(("expect.json", true, "sem expectativas: só repetiu (" + Summary() + ")"));
            _results.Add(result);
            Debug.Log($"[LinkSessionTests] {result.session}: {(result.Passed ? "PASSOU" : "FALHOU")} — " +
                      string.Join("; ", result.checks.Select(c => (c.ok ? "✓ " : "✗ ") + c.name + ": " + c.message)));
            if (_queue.Count == 0) Finish();
        }

        static string Summary() =>
            string.Join(", ", _detections.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key}×{kv.Value}")) +
            (_placed.Count > 0 ? "; etiquetas: " + string.Join(", ", _placed.Select(kv => $"{kv.Key}×{kv.Value.Count}")) : "");

        static void Finish()
        {
            LinkHooks.DetectNow?.Invoke(); // o app volta ao próprio ritmo de detecção
            var sb = new StringBuilder();
            int failed = _results.Count(r => !r.Passed);
            sb.AppendLine($"Testes de sessão: {_results.Count - failed}/{_results.Count} passaram");
            foreach (var r in _results)
            {
                sb.AppendLine($"  {(r.Passed ? "✓" : "✗")} {r.session} ({r.seconds:0} s)");
                foreach (var c in r.checks) sb.AppendLine($"      {(c.ok ? "✓" : "✗")} {c.name}: {c.message}");
            }
            LastReport = sb.ToString().TrimEnd();
            WriteJUnit();
            Debug.Log("[LinkSessionTests] " + LastReport);
            if (EditorPrefs.GetBool(CliKey, false))
            {
                EditorPrefs.SetBool(CliKey, false);
                EditorApplication.ExitPlaymode();
                EditorApplication.delayCall += () => EditorApplication.Exit(failed == 0 && _results.Count > 0 ? 0 : 1);
            }
        }

        static string Xml(string s) => System.Security.SecurityElement.Escape(s ?? "");

        static void WriteJUnit()
        {
            string dir = Path.Combine(Directory.GetCurrentDirectory(), "TestResults");
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            int tests = _results.Sum(r => r.checks.Count), failures = _results.Sum(r => r.checks.Count(c => !c.ok));
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine($"<testsuites name=\"Link para Mac — sessões\" tests=\"{tests}\" failures=\"{failures}\">");
            foreach (var r in _results)
            {
                sb.AppendLine($"  <testsuite name=\"{Xml(r.session)}\" tests=\"{r.checks.Count}\" failures=\"{r.checks.Count(c => !c.ok)}\" time=\"{r.seconds.ToString("0.0", Inv)}\">");
                foreach (var c in r.checks)
                {
                    sb.Append($"    <testcase classname=\"{Xml(r.session)}\" name=\"{Xml(c.name)}\">");
                    if (!c.ok) sb.Append($"<failure message=\"{Xml(c.message)}\"/>");
                    sb.AppendLine("</testcase>");
                }
                sb.AppendLine("  </testsuite>");
            }
            sb.AppendLine("</testsuites>");
            File.WriteAllText(Path.Combine(dir, "link-sessions.xml"), sb.ToString());
        }
    }
}
