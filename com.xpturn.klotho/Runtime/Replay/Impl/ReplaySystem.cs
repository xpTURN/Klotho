using System;
using xpTURN.Klotho.Logging;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Replay
{
    /// <summary>
    /// Unified replay system implementation
    /// Combines recording and playback functionality with file I/O
    /// </summary>
    public class ReplaySystem : IReplaySystem
    {
        // RPLY magic number for ReplayData (uncompressed file)
        private const uint RPLY_MAGIC = 0x52504C59;

        private IKLogger _logger;
        private readonly ReplayRecorder _recorder;
        private readonly ReplayPlayer _player;
        private IReplayData _currentReplayData;
        private readonly ICommandFactory _commandFactory;

        public ReplaySystem() : this(new Core.CommandFactory(), null)
        {
        }

        public ReplaySystem(ICommandFactory commandFactory, IKLogger logger)
        {
            _logger = logger;
            _commandFactory = commandFactory;

            _recorder = new ReplayRecorder(commandFactory, _logger);
            _player = new ReplayPlayer();
            
            // Forward recorder events
            _recorder.OnRecordingStarted += () => OnRecordingStarted?.Invoke();
            _recorder.OnRecordingStopped += (data) =>
            {
                _currentReplayData = data;
                OnRecordingStopped?.Invoke(data);
            };
            
            // Forward player events
            _player.OnTickPlayed += (tick, commands) => OnTickPlayed?.Invoke(tick, commands);
            _player.OnPlaybackFinished += () => OnPlaybackFinished?.Invoke();
            _player.OnSeekCompleted += (tick) => OnSeekCompleted?.Invoke(tick);
        }

        #region IReplayRecorder Implementation

        ReplayState IReplayRecorder.State => _recorder.State;
        int IReplayRecorder.CurrentTick => _recorder.CurrentTick;

        public event Action OnRecordingStarted;
        public event Action<IReplayData> OnRecordingStopped;

        public void StartRecording(int playerCount, ISimulationConfig simConfig, int randomSeed)
        {
            _recorder.StartRecording(playerCount, simConfig, randomSeed);
        }

        public void RecordTick(int tick, List<ICommand> commands)
        {
            _recorder.RecordTick(tick, commands);
        }

        public IReplayData StopRecording(int totalTicks, ReplayEndReason reason = ReplayEndReason.Normal)
        {
            var data = _recorder.StopRecording(totalTicks, reason);
            _currentReplayData = data;
            return data;
        }

        #endregion

        #region IReplayPlayer Implementation

        ReplayState IReplayPlayer.State => _player.State;
        int IReplayPlayer.CurrentTick => _player.CurrentTick;
        public int TotalTicks => _player.TotalTicks;
        
        public ReplaySpeed Speed
        {
            get => _player.Speed;
            set => _player.Speed = value;
        }

        public float Progress => _player.Progress;
        public float Accumulator => _player.Accumulator;

        public event Action<int, IReadOnlyList<ICommand>> OnTickPlayed;
        public event Action OnPlaybackFinished;
        public event Action<int> OnSeekCompleted;

        public void Load(IReplayData replayData, IKLogger logger)
        {
            _currentReplayData = replayData;
            _player.Load(replayData, _logger);
        }

        public void Play()
        {
            _player.Play();
        }

        public void Pause()
        {
            _player.Pause();
        }

        public void Resume()
        {
            _player.Resume();
        }

        public void Stop()
        {
            _player.Stop();
        }

        public void SeekToTick(int tick)
        {
            _player.SeekToTick(tick);
        }

        public void SeekToProgress(float progress)
        {
            _player.SeekToProgress(progress);
        }

        public IReadOnlyList<ICommand> GetCurrentTickCommands()
        {
            return _player.GetCurrentTickCommands();
        }

        public void Update(float deltaTime)
        {
            _player.Update(deltaTime);
        }

        #endregion

        #region IReplaySystem Implementation

        public bool IsRecording => _recorder.State == ReplayState.Recording;
        public bool IsPlaying => _player.State == ReplayState.Playing;
        public IReplayData CurrentReplayData => _currentReplayData;

        public void SetGameCustomData(byte[] data)
        {
            _recorder.SetGameCustomData(data);
        }

        public event Action<byte[], long> OnInitialStateSnapshotSet;

        public void SetInitialStateSnapshot(byte[] snapshot, long hash, int tick)
        {
            _recorder.SetInitialStateSnapshot(snapshot, hash, tick);
            // The event stays (snapshot, hash). The engine's broadcast cache labels what it stores with
            // tick 0 and sends that label on the wire, so widening this event is not a free correction.
            OnInitialStateSnapshotSet?.Invoke(snapshot, hash);
        }

        public void SetInitialRoster(IReadOnlyList<int> roster)
        {
            _recorder.SetInitialRoster(roster);
        }

        public void SetInitialEntitlements(IReadOnlyList<byte[]> perRosterEntry)
        {
            _recorder.SetInitialEntitlements(perRosterEntry);
        }

        public void SetReproductionAnchors(long layoutFingerprint, long staticColliderFingerprint, long navFingerprint, long gameFingerprint)
        {
            _recorder.SetReproductionAnchors(layoutFingerprint, staticColliderFingerprint, navFingerprint, gameFingerprint);
        }

        public void SaveToFile(string filePath, bool dumpJson = false)
        {
            if (_currentReplayData == null)
            {
                _logger?.KWarning($"[ReplaySystem] No replay data to save");
                return;
            }

            try
            {
                // Ensure the directory exists
                string directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                byte[] data = _currentReplayData.Serialize();
                File.WriteAllBytes(filePath, data);

                _logger?.KInformation($"[ReplaySystem] Replay saved: {filePath} ({data.Length} bytes)");

                if (dumpJson)
                {
                    string jsonPath = Path.ChangeExtension(filePath, ".json");
                    string json = DumpReplayToJson(_currentReplayData);
                    File.WriteAllText(jsonPath, json);
                    _logger?.KInformation($"[ReplaySystem] Replay JSON dumped: {jsonPath} ({json.Length} chars)");
                }
            }
            catch (Exception e)
            {
                _logger?.KError($"[ReplaySystem] Failed to save replay: {e.Message}");
            }
        }

        #region JSON Dump (Debug)

        // For debug dumps only — uses reflection/GC. Do not call from runtime code paths.
        private const int JsonDumpMaxDepth = 8;
        private const string FP64FullName = "xpTURN.Klotho.Deterministic.Math.FP64";

        private static string DumpReplayToJson(IReplayData replayData)
        {
            var sb = new StringBuilder(16 * 1024);
            sb.Append('{');
            AppendNewLine(sb, 1);
            AppendJsonString(sb, "Metadata");
            sb.Append(": ");
            AppendJsonValue(sb, replayData.Metadata, 1, JsonDumpMaxDepth);
            sb.Append(',');
            AppendNewLine(sb, 1);
            AppendJsonString(sb, "Ticks");
            sb.Append(": [");

            var metadata = replayData.Metadata;
            bool firstTick = true;
            for (int tick = 0; tick <= metadata.TotalTicks; tick++)
            {
                var commands = replayData.GetCommandsForTick(tick);
                if (commands.Count == 0) continue;
                if (!firstTick) sb.Append(',');
                firstTick = false;
                AppendNewLine(sb, 2);
                sb.Append('{');
                AppendNewLine(sb, 3);
                AppendJsonString(sb, "Tick");
                sb.Append(": ");
                sb.Append(tick.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                AppendNewLine(sb, 3);
                AppendJsonString(sb, "Commands");
                sb.Append(": [");
                for (int i = 0; i < commands.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    AppendNewLine(sb, 4);
                    AppendJsonValue(sb, commands[i], 4, JsonDumpMaxDepth);
                }
                if (commands.Count > 0) AppendNewLine(sb, 3);
                sb.Append(']');
                AppendNewLine(sb, 2);
                sb.Append('}');
            }
            if (!firstTick) AppendNewLine(sb, 1);
            sb.Append(']');
            sb.Append('\n');
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendJsonValue(StringBuilder sb, object value, int indent, int maxDepth)
        {
            if (value == null) { sb.Append("null"); return; }
            if (maxDepth <= 0) { sb.Append("null"); return; }

            var type = value.GetType();

            if (type == typeof(bool)) { sb.Append((bool)value ? "true" : "false"); return; }
            if (type == typeof(string)) { AppendJsonString(sb, (string)value); return; }
            if (type == typeof(byte[])) { AppendJsonString(sb, Convert.ToBase64String((byte[])value)); return; }
            if (type.IsEnum) { AppendJsonString(sb, value.ToString()); return; }

            if (type.IsPrimitive)
            {
                if (type == typeof(float))
                    sb.Append(((float)value).ToString("R", CultureInfo.InvariantCulture));
                else if (type == typeof(double))
                    sb.Append(((double)value).ToString("R", CultureInfo.InvariantCulture));
                else
                    sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }

            // FP64 — emit as float for readability
            if (type.FullName == FP64FullName)
            {
                var toFloat = type.GetMethod("ToFloat", BindingFlags.Public | BindingFlags.Instance);
                if (toFloat != null)
                {
                    var f = (float)toFloat.Invoke(value, null);
                    sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                    return;
                }
            }

            if (value is IEnumerable enumerable)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in enumerable)
                {
                    if (!first) sb.Append(',');
                    AppendNewLine(sb, indent + 1);
                    AppendJsonValue(sb, item, indent + 1, maxDepth - 1);
                    first = false;
                }
                if (!first) AppendNewLine(sb, indent);
                sb.Append(']');
                return;
            }

            AppendJsonObject(sb, value, type, indent, maxDepth);
        }

        private static void AppendJsonObject(StringBuilder sb, object value, Type type, int indent, int maxDepth)
        {
            sb.Append('{');
            bool first = true;
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!first) sb.Append(',');
                AppendNewLine(sb, indent + 1);
                AppendJsonString(sb, f.Name);
                sb.Append(": ");
                object fv;
                try { fv = f.GetValue(value); } catch { fv = null; }
                AppendJsonValue(sb, fv, indent + 1, maxDepth - 1);
                first = false;
            }
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead) continue;
                if (p.GetIndexParameters().Length > 0) continue;
                if (!first) sb.Append(',');
                AppendNewLine(sb, indent + 1);
                AppendJsonString(sb, p.Name);
                sb.Append(": ");
                object pv;
                try { pv = p.GetValue(value); } catch { pv = null; }
                AppendJsonValue(sb, pv, indent + 1, maxDepth - 1);
                first = false;
            }
            if (!first) AppendNewLine(sb, indent);
            sb.Append('}');
        }

        private static void AppendNewLine(StringBuilder sb, int indent)
        {
            sb.Append('\n');
            for (int i = 0; i < indent; i++) sb.Append("  ");
        }

        private static void AppendJsonString(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (s != null)
            {
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20) sb.AppendFormat("\\u{0:X4}", (int)c);
                            else sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
        }

        #endregion

        public void LoadFromFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("Replay file path is required.", nameof(filePath));
            if (!File.Exists(filePath))
                throw new ReplayLoadException($"Replay file not found: {filePath}");

            byte[] fileData;
            try { fileData = File.ReadAllBytes(filePath); }
            catch (Exception e)
            {
                throw new ReplayLoadException($"Replay file read failed: {filePath}", e);
            }

            ReplayData replayData;
            try
            {
                // Replay files are uncompressed and self-framed by the RPLY magic header.
                if (fileData.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(fileData) != RPLY_MAGIC)
                    throw new ReplayLoadException($"Replay file has no RPLY header (unrecognized or legacy compressed format): {filePath}");
                byte[] raw = fileData;

                replayData = new ReplayData(_commandFactory);
                replayData.Deserialize(raw);
                _player.Load(replayData, _logger);
            }
            catch (Exception e)
            {
                // Atomicity: _currentReplayData is only assigned after every step above succeeds, so
                // a partial failure leaves the previous _currentReplayData / _player state untouched.
                // Carry the reason into the outer message: callers are documented to show e.Message
                // (Docs/Replay.md §8), and "deserialize failed" alone hides "re-record this replay".
                throw new ReplayLoadException($"Replay deserialize failed: {filePath} — {e.Message}", e);
            }

            _currentReplayData = replayData;
            _logger?.KInformation($"[ReplaySystem] Replay loaded: {filePath} ({fileData.Length} bytes)");
        }

        #endregion

        /// <summary>
        /// Cancels the recording
        /// </summary>
        public void CancelRecording()
        {
            _recorder.CancelRecording();
        }

        /// <summary>
        /// Steps forward by one tick (for frame-by-frame playback)
        /// </summary>
        public void StepForward()
        {
            _player.StepForward();
        }

        /// <summary>
        /// Steps backward by one tick
        /// </summary>
        public void StepBackward()
        {
            _player.StepBackward();
        }

        /// <summary>
        /// Returns the current state (playback state takes priority over recording)
        /// </summary>
        public ReplayState State
        {
            get
            {
                if (_player.State == ReplayState.Playing || _player.State == ReplayState.Paused)
                    return _player.State;
                return _recorder.State;
            }
        }
    }
}
