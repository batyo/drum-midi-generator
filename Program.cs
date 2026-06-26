// =====================================================================
// DrumMidiGenerator (Omnizart版)
// ---------------------------------------------------------------------
// パイプライン:
//   1. FFmpeg        : MP3 -> WAV (44.1kHz / 16bit / stereo)
//   2. Demucs        : ドラムスステムを分離 (外部プロセス, Python venv)
//   3. Omnizart      : ドラムWAVをディープラーニングで解析しMIDI化 (Dockerコンテナ)
//   4. C# 整形       : Omnizart出力MIDIを読み込み、Cakewalk向けに整形して再出力
//   5. Cakewalk SONAR: MIDIを読み込む
//
// 必要環境:
//   - .NET 8 SDK
//   - FFmpeg (ffmpeg.exe がPATH上にあること、または --ffmpeg で指定)
//   - Python + Demucs (Demucs専用venv。例: C:\tools\demucs-env)
//   - Docker Desktop (mctlab/omnizart イメージ使用)
//       事前に1回だけ: docker pull mctlab/omnizart:latest
//
// 使い方:
//   dotnet run -- "C:\path\to\song.mp3" 128
//   dotnet run -- "C:\path\to\song.mp3" 128 --output "MyDrumTrack" --keep-temp
//   dotnet run -- "C:\path\to\song.mp3" 128 --python "C:\tools\demucs-env\Scripts\python.exe"
//
// 出力:
//   入力MP3と同じフォルダに "<元ファイル名>_drums.mid" が生成されます。
// =====================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace DrumMidiGenerator
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("使い方: DrumMidiGenerator <input.mp3> [bpm] [options]");
                Console.WriteLine("  bpm              : 元曲のテンポ (省略時 128。Omnizart出力には直接使われず、参考表示用)");
                Console.WriteLine("  --ffmpeg path    : ffmpeg実行ファイルのパス (省略時 'ffmpeg')");
                Console.WriteLine("  --python path    : Demucs用python実行ファイルのパス (省略時 'python')");
                Console.WriteLine("  --model name     : Demucsモデル名 (省略時 'htdemucs')");
                Console.WriteLine("  --docker path    : docker実行ファイルのパス (省略時 'docker')");
                Console.WriteLine("  --docker-image n : Omnizart Dockerイメージ名 (省略時 'mctlab/omnizart:latest')");
                Console.WriteLine("  --velocity-scale x : 出力ベロシティの倍率 (省略時 1.0)");
                Console.WriteLine("  --output path    : 出力MIDIファイルのパス/ファイル名 (省略時 '<入力名>_drums.mid')");
                Console.WriteLine("  --keep-temp      : 作業用の中間ファイルを削除しない");
                return 1;
            }

            string inputMp3 = args[0];
            double bpm = 128.0;
            string ffmpegPath = "ffmpeg";
            string pythonPath = "python";
            string demucsModel = "htdemucs";
            string dockerPath = "docker";
            string dockerImage = "mctlab/omnizart:latest";
            double velocityScale = 1.0;
            string? outputPath = null;
            bool keepTemp = false;

            for (int i = 1; i < args.Length; i++)
            {
                if (i == 1 && double.TryParse(args[i], out var b))
                {
                    bpm = b;
                    continue;
                }
                switch (args[i])
                {
                    case "--ffmpeg" when i + 1 < args.Length: ffmpegPath = args[++i]; break;
                    case "--python" when i + 1 < args.Length: pythonPath = args[++i]; break;
                    case "--model" when i + 1 < args.Length: demucsModel = args[++i]; break;
                    case "--docker" when i + 1 < args.Length: dockerPath = args[++i]; break;
                    case "--docker-image" when i + 1 < args.Length: dockerImage = args[++i]; break;
                    case "--velocity-scale" when i + 1 < args.Length: velocityScale = double.Parse(args[++i]); break;
                    case "--output" when i + 1 < args.Length: outputPath = args[++i]; break;
                    case "--keep-temp": keepTemp = true; break;
                }
            }

            if (!File.Exists(inputMp3))
            {
                Console.WriteLine($"入力ファイルが見つかりません: {inputMp3}");
                return 1;
            }

            string inputDir = Path.GetDirectoryName(Path.GetFullPath(inputMp3)) ?? ".";
            string baseName = Path.GetFileNameWithoutExtension(inputMp3);
            string workDir = Path.Combine(inputDir, baseName + "_drummidi_work");
            Directory.CreateDirectory(workDir);

            try
            {
                // --- 1. FFmpeg: MP3 -> WAV ---
                Console.WriteLine("[1/4] FFmpegでMP3をWAVに変換中...");
                string wavPath = Path.Combine(workDir, baseName + ".wav");
                FFmpegConverter.ConvertToWav(inputMp3, wavPath, ffmpegPath);
                Console.WriteLine($"  -> {wavPath}");

                // --- 2. Demucs: ドラムスを分離 ---
                Console.WriteLine("[2/4] Demucsでドラムスを分離中... (数分かかる場合があります)");
                string drumsWavPath = DemucsSeparator.SeparateDrums(wavPath, workDir, pythonPath, demucsModel);
                Console.WriteLine($"  -> {drumsWavPath}");

                // --- 3. Omnizart: ディープラーニングでドラム解析 -> MIDI ---
                Console.WriteLine("[3/4] Omnizart (Docker) でドラムを解析中... (初回はモデル読み込みに時間がかかります)");
                string omnizartMidiPath = OmnizartTranscriber.Transcribe(drumsWavPath, workDir, dockerPath, dockerImage);
                Console.WriteLine($"  -> {omnizartMidiPath}");

                // --- 4. MIDI読み込み + 整形 + 再出力 ---
                Console.WriteLine("[4/4] MIDIをCakewalk向けに整形中...");
                var notes = MidiReader.ReadDrumNotes(omnizartMidiPath);
                Console.WriteLine($"  読み込みノート数: {notes.Count}");

                if (Math.Abs(velocityScale - 1.0) > 1e-9)
                {
                    foreach (var n in notes)
                        n.Velocity = (int)Math.Clamp(n.Velocity * velocityScale, 1, 127);
                }

                int kicks = notes.Count(n => n.MidiNote == DrumMap.Kick);
                int snares = notes.Count(n => n.MidiNote == DrumMap.Snare);
                int hihats = notes.Count(n => n.MidiNote == DrumMap.ClosedHiHat || n.MidiNote == DrumMap.OpenHiHat);
                int others = notes.Count - kicks - snares - hihats;
                Console.WriteLine($"  内訳: Kick:{kicks} / Snare:{snares} / HiHat:{hihats} / その他:{others}");

                string midiPath = ResolveOutputPath(outputPath, inputDir, baseName);
                MidiWriter.Write(midiPath, notes, bpm);
                Console.WriteLine($"完了: {midiPath}");

                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"エラー: {ex.Message}");
                return 1;
            }
            finally
            {
                // 正常終了・異常終了どちらの場合も作業フォルダを確実に削除する
                if (!keepTemp)
                {
                    try
                    {
                        if (Directory.Exists(workDir))
                        {
                            Directory.Delete(workDir, true);
                            Console.WriteLine("作業フォルダを削除しました。");
                        }
                    }
                    catch (Exception ex)
                    {
                        // 削除失敗は警告に留める (ファイルロック中など)
                        Console.WriteLine($"警告: 作業フォルダの削除に失敗しました ({workDir}): {ex.Message}");
                        Console.WriteLine("  手動で削除してください。");
                    }
                }
                else
                {
                    Console.WriteLine($"中間ファイルを保持しました: {workDir}");
                }
            }
        }

        /// <summary>
        /// 出力先MIDIファイルのパスを決定する。
        ///  - 未指定時: "<入力ファイル名>_drums.mid" を入力フォルダに保存
        ///  - ファイル名のみ指定時 (パス区切りなし): 入力フォルダ内にそのファイル名で保存
        ///  - パス付きで指定時: そのパスをそのまま使用 (相対パスは現在の作業ディレクトリ基準)
        ///  - 拡張子が ".mid" / ".midi" でない場合は ".mid" を付加する
        /// </summary>
        private static string ResolveOutputPath(string? outputPath, string inputDir, string baseName)
        {
            string path;
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                path = Path.Combine(inputDir, baseName + "_drums.mid");
            }
            else if (!outputPath.Contains(Path.DirectorySeparatorChar) && !outputPath.Contains(Path.AltDirectorySeparatorChar))
            {
                path = Path.Combine(inputDir, outputPath);
            }
            else
            {
                path = Path.GetFullPath(outputPath);
            }

            string ext = Path.GetExtension(path);
            if (!ext.Equals(".mid", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".midi", StringComparison.OrdinalIgnoreCase))
            {
                path += ".mid";
            }

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            return path;
        }
    }

    // =====================================================================
    // FFmpeg: MP3 -> WAV 変換
    // =====================================================================
    static class FFmpegConverter
    {
        public static void ConvertToWav(string inputMp3, string outputWav, string ffmpegPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-y -i \"{inputMp3}\" -ar 44100 -ac 2 -sample_fmt s16 \"{outputWav}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Process? process;
            try
            {
                process = Process.Start(psi);
            }
            catch (Exception ex)
            {
                throw new Exception($"FFmpegを起動できません ('{ffmpegPath}'). PATHを確認するか --ffmpeg で実行ファイルを指定してください。詳細: {ex.Message}");
            }

            if (process == null)
                throw new Exception("FFmpegプロセスの起動に失敗しました。");

            process.WaitForExit();

            if (process.ExitCode != 0 || !File.Exists(outputWav))
                throw new Exception($"FFmpegの実行に失敗しました (終了コード: {process.ExitCode})。");
        }
    }

    // =====================================================================
    // Demucs: ドラムスステムの分離 (外部プロセス呼び出し)
    // =====================================================================
    static class DemucsSeparator
    {
        public static string SeparateDrums(string inputWav, string outputDir, string pythonPath, string model)
        {
            var psi = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = $"-m demucs -n {model} --two-stems=drums -o \"{outputDir}\" \"{inputWav}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            Process? process;
            var errorLines = new List<string>();
            try
            {
                process = Process.Start(psi);
            }
            catch (Exception ex)
            {
                throw new Exception($"Pythonを起動できません ('{pythonPath}'). Demucs用venvのpython.exeを --python で指定してください。詳細: {ex.Message}");
            }

            if (process == null)
                throw new Exception("Demucsプロセスの起動に失敗しました。");

            process.OutputDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine(e.Data); };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                Console.WriteLine(e.Data);
                errorLines.Add(e.Data);
                if (errorLines.Count > 20) errorLines.RemoveAt(0);
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                string detail = errorLines.Count > 0
                    ? "\n--- Demucs出力(末尾) ---\n" + string.Join("\n", errorLines)
                    : "";
                throw new Exception($"Demucsの実行に失敗しました (終了コード: {process.ExitCode})。{detail}");
            }

            string trackName = Path.GetFileNameWithoutExtension(inputWav);
            string drumsPath = Path.Combine(outputDir, model, trackName, "drums.wav");

            if (!File.Exists(drumsPath))
                throw new FileNotFoundException($"Demucsの出力ファイルが見つかりません: {drumsPath}");

            return drumsPath;
        }
    }

    // =====================================================================
    // Omnizart: ドラムのディープラーニング解析 (Dockerコンテナ呼び出し)
    //
    //   docker run --rm -v "<workDir>:/data" <image> omnizart drum transcribe
    //       /data/<wavファイル名> --output /data/<出力ファイル名>
    //
    // Omnizartは内部でCNN+Attentionベースのモデルを使い、A2MDデータセット
    // (約34時間のポップス楽曲)で学習済みのチェックポイントから推論する。
    // 自前のFFTヒューリスティック解析よりも複雑なパターンへの頑健性が高い。
    // =====================================================================
    static class OmnizartTranscriber
    {
        public static string Transcribe(string drumsWavPath, string workDir, string dockerPath, string dockerImage)
        {
            // Dockerにマウントするボリュームのホスト側パスと、コンテナ内でのファイル名を用意
            string wavFileName = Path.GetFileName(drumsWavPath);
            string outputFileName = Path.GetFileNameWithoutExtension(drumsWavPath) + "_omnizart.mid";

            // Omnizartへの入力WAVはworkDir直下に既に存在する想定 (Demucsの出力をコピーしておく)
            string containerInputDir = "/data";
            string hostMountDir = Path.GetFullPath(workDir);

            // drums.wav は Demucs の出力サブフォルダ内にあるため、
            // Dockerからマウントしやすいよう workDir 直下にコピーする
            string localCopyPath = Path.Combine(workDir, wavFileName);
            if (!string.Equals(Path.GetFullPath(localCopyPath), Path.GetFullPath(drumsWavPath), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(drumsWavPath, localCopyPath, overwrite: true);
            }

            string containerWavPath = $"{containerInputDir}/{wavFileName}";
            string containerOutputPath = $"{containerInputDir}/{outputFileName}";

            // Windowsのパスをdocker -vで使える形式に変換 (例: C:\foo\bar -> //c/foo/bar)
            string dockerVolumeArg = ToDockerVolumePath(hostMountDir);

            var psi = new ProcessStartInfo
            {
                FileName = dockerPath,
                Arguments = $"run --rm -v \"{dockerVolumeArg}:{containerInputDir}\" {dockerImage} " +
                            $"omnizart drum transcribe \"{containerWavPath}\" --output \"{containerOutputPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            Process? process;
            var errorLines = new List<string>();
            try
            {
                process = Process.Start(psi);
            }
            catch (Exception ex)
            {
                throw new Exception($"Dockerを起動できません ('{dockerPath}'). Docker Desktopが起動しているか確認してください。詳細: {ex.Message}");
            }

            if (process == null)
                throw new Exception("Omnizart (Docker) プロセスの起動に失敗しました。");

            process.OutputDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine(e.Data); };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                Console.WriteLine(e.Data);
                errorLines.Add(e.Data);
                if (errorLines.Count > 25) errorLines.RemoveAt(0);
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                string detail = errorLines.Count > 0
                    ? "\n--- Omnizart(Docker)出力(末尾) ---\n" + string.Join("\n", errorLines)
                    : "";
                throw new Exception(
                    $"Omnizartの実行に失敗しました (終了コード: {process.ExitCode})。" +
                    $"Docker Desktopの起動状態、および 'docker pull {dockerImage}' 実施済みかを確認してください。{detail}");
            }

            string resultPath = Path.Combine(workDir, outputFileName);
            if (!File.Exists(resultPath))
                throw new FileNotFoundException($"Omnizartの出力ファイルが見つかりません: {resultPath}");

            return resultPath;
        }

        /// <summary>
        /// Windowsパス (例: C:\Users\foo\bar) を Docker Desktop (WSL2バックエンド) が
        /// 解釈できる形式 (例: //c/Users/foo/bar) に変換する。
        /// </summary>
        private static string ToDockerVolumePath(string windowsPath)
        {
            string full = Path.GetFullPath(windowsPath).Replace('\\', '/');
            if (full.Length >= 2 && full[1] == ':')
            {
                char drive = char.ToLowerInvariant(full[0]);
                return $"//{drive}{full.Substring(2)}";
            }
            return full;
        }
    }

    // =====================================================================
    // GM ドラムマップ (使用するノート番号)
    // =====================================================================
    static class DrumMap
    {
        public const int Kick = 36;        // Bass Drum 1
        public const int Snare = 38;       // Acoustic Snare
        public const int ClosedHiHat = 42; // Closed Hi-Hat
        public const int OpenHiHat = 46;   // Open Hi-Hat
        public const int CrashCymbal = 49; // Crash Cymbal 1
        public const int RideCymbal = 51;  // Ride Cymbal 1
        public const int LowTom = 45;
        public const int MidTom = 47;
        public const int HighTom = 50;
    }

    // =====================================================================
    // 読み込んだ/書き出すドラムノート1件分
    // =====================================================================
    class DrumNote
    {
        public double StartSeconds;
        public double DurationSeconds;
        public int MidiNote;
        public int Velocity;
    }

    // =====================================================================
    // 標準MIDIファイル(SMF)読み込み (Omnizartの出力MIDIをパースするため)
    //   - Format 0 / Format 1 の両方に対応
    //   - 全トラックのNote On/Offイベントを統合して読み込む
    //   - テンポ情報からtick単位を秒に変換する
    // =====================================================================
    static class MidiReader
    {
        public static List<DrumNote> ReadDrumNotes(string path)
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);

            if (new string(ReadChars(br, 4)) != "MThd")
                throw new InvalidDataException("MThdチャンクが見つかりません。MIDIファイルではない可能性があります。");

            int headerLen = ReadInt32BE(br);
            short format = ReadInt16BE(br);
            short numTracks = ReadInt16BE(br);
            short division = ReadInt16BE(br);
            if (headerLen > 6) br.ReadBytes(headerLen - 6);

            if (division < 0)
                throw new NotSupportedException("SMPTEタイムフォーマットのMIDIには対応していません。");

            int ticksPerQuarter = division;

            // 全トラックのイベントを (tick, ...) で集約
            var tempoChanges = new List<(long tick, int microsPerQuarter)> { (0, 500000) }; // デフォルト120BPM
            var noteEvents = new List<(long tick, int note, int velocity, bool isOn)>();

            for (int t = 0; t < numTracks; t++)
            {
                string chunkId = new string(ReadChars(br, 4));
                int chunkLen = ReadInt32BE(br);
                long chunkEnd = br.BaseStream.Position + chunkLen;

                if (chunkId != "MTrk")
                {
                    br.BaseStream.Seek(chunkLen, SeekOrigin.Current);
                    continue;
                }

                long tick = 0;
                byte runningStatus = 0;

                while (br.BaseStream.Position < chunkEnd)
                {
                    long delta = ReadVarLen(br);
                    tick += delta;

                    byte statusByte = br.ReadByte();
                    byte status;

                    if (statusByte < 0x80)
                    {
                        // ランニングステータス: ステータスバイト省略、直前のものを再利用
                        status = runningStatus;
                        br.BaseStream.Seek(-1, SeekOrigin.Current);
                    }
                    else
                    {
                        status = statusByte;
                        runningStatus = status;
                    }

                    int hi = status & 0xF0;

                    if (status == 0xFF)
                    {
                        // メタイベント
                        byte metaType = br.ReadByte();
                        long len = ReadVarLen(br);
                        byte[] data = br.ReadBytes((int)len);

                        if (metaType == 0x51 && data.Length == 3) // Set Tempo
                        {
                            int micros = (data[0] << 16) | (data[1] << 8) | data[2];
                            tempoChanges.Add((tick, micros));
                        }
                    }
                    else if (status == 0xF0 || status == 0xF7)
                    {
                        // SysExイベント
                        long len = ReadVarLen(br);
                        br.ReadBytes((int)len);
                    }
                    else if (hi == 0x90 || hi == 0x80)
                    {
                        // Note On / Note Off
                        int note = br.ReadByte();
                        int velocity = br.ReadByte();
                        bool isOn = hi == 0x90 && velocity > 0;
                        noteEvents.Add((tick, note, velocity, isOn));
                    }
                    else
                    {
                        // その他のチャンネルメッセージ (Control Change等): データバイト数をスキップ
                        int dataBytes = GetChannelMessageDataBytes(hi);
                        for (int k = 0; k < dataBytes; k++) br.ReadByte();
                    }
                }

                br.BaseStream.Position = chunkEnd;
            }

            tempoChanges.Sort((a, b) => a.tick.CompareTo(b.tick));

            // tick -> 秒変換用のヘルパー
            double TickToSeconds(long targetTick)
            {
                double seconds = 0;
                long lastTick = 0;
                int currentMicros = 500000;

                foreach (var (tick, micros) in tempoChanges)
                {
                    if (tick >= targetTick) break;
                    seconds += (tick - lastTick) * (currentMicros / 1_000_000.0) / ticksPerQuarter;
                    lastTick = tick;
                    currentMicros = micros;
                }
                seconds += (targetTick - lastTick) * (currentMicros / 1_000_000.0) / ticksPerQuarter;
                return seconds;
            }

            // Note On/Offをペアリングしてノート区間を作る (note番号ごとにスタックで対応)
            noteEvents.Sort((a, b) => a.tick.CompareTo(b.tick));
            var pending = new Dictionary<int, Queue<(long tick, int velocity)>>();
            var notes = new List<DrumNote>();

            foreach (var (tick, note, velocity, isOn) in noteEvents)
            {
                if (isOn)
                {
                    if (!pending.TryGetValue(note, out var q))
                    {
                        q = new Queue<(long, int)>();
                        pending[note] = q;
                    }
                    q.Enqueue((tick, velocity));
                }
                else
                {
                    if (pending.TryGetValue(note, out var q) && q.Count > 0)
                    {
                        var (onTick, onVelocity) = q.Dequeue();
                        double startSec = TickToSeconds(onTick);
                        double endSec = TickToSeconds(tick);
                        notes.Add(new DrumNote
                        {
                            StartSeconds = startSec,
                            DurationSeconds = Math.Max(0.01, endSec - startSec),
                            MidiNote = note,
                            Velocity = onVelocity
                        });
                    }
                }
            }

            notes.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
            return notes;
        }

        private static int GetChannelMessageDataBytes(int statusHighNibble) => statusHighNibble switch
        {
            0xC0 or 0xD0 => 1, // Program Change, Channel Pressure
            _ => 2             // Note On/Off, Control Change, Pitch Bend, Polyphonic Aftertouch
        };

        private static char[] ReadChars(BinaryReader br, int count) => br.ReadChars(count);

        private static int ReadInt32BE(BinaryReader br)
        {
            byte[] b = br.ReadBytes(4);
            return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
        }

        private static short ReadInt16BE(BinaryReader br)
        {
            byte[] b = br.ReadBytes(2);
            return (short)((b[0] << 8) | b[1]);
        }

        private static long ReadVarLen(BinaryReader br)
        {
            long value = 0;
            byte b;
            do
            {
                b = br.ReadByte();
                value = (value << 7) | (uint)(b & 0x7F);
            } while ((b & 0x80) != 0);
            return value;
        }
    }

    // =====================================================================
    // MIDIファイル (SMF Format 1) 書き出し
    //   Track0: テンポ + 拍子
    //   Track1: ドラムノート (Channel 10 = GMドラムチャンネル)
    // =====================================================================
    static class MidiWriter
    {
        private const int TicksPerQuarter = 480;

        public static void Write(string path, List<DrumNote> notes, double bpm)
        {
            using var fs = new FileStream(path, FileMode.Create);
            using var bw = new BinaryWriter(fs);

            WriteHeader(bw, format: 1, numTracks: 2, division: TicksPerQuarter);

            var tempoTrack = BuildTempoTrack(bpm);
            WriteTrackChunk(bw, tempoTrack);

            var drumTrack = BuildDrumTrack(notes, bpm);
            WriteTrackChunk(bw, drumTrack);
        }

        private static void WriteHeader(BinaryWriter bw, short format, short numTracks, short division)
        {
            bw.Write(new[] { (byte)'M', (byte)'T', (byte)'h', (byte)'d' });
            WriteInt32BE(bw, 6);
            WriteInt16BE(bw, format);
            WriteInt16BE(bw, numTracks);
            WriteInt16BE(bw, division);
        }

        private static byte[] BuildTempoTrack(double bpm)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            // 拍子記号 4/4
            WriteVarLen(bw, 0);
            bw.Write((byte)0xFF); bw.Write((byte)0x58); bw.Write((byte)0x04);
            bw.Write((byte)4); bw.Write((byte)2); bw.Write((byte)24); bw.Write((byte)8);

            // テンポ (マイクロ秒/4分音符)
            int microsPerQuarter = (int)Math.Round(60000000.0 / bpm);
            WriteVarLen(bw, 0);
            bw.Write((byte)0xFF); bw.Write((byte)0x51); bw.Write((byte)0x03);
            bw.Write((byte)((microsPerQuarter >> 16) & 0xFF));
            bw.Write((byte)((microsPerQuarter >> 8) & 0xFF));
            bw.Write((byte)(microsPerQuarter & 0xFF));

            WriteEndOfTrack(bw);
            return ms.ToArray();
        }

        private static byte[] BuildDrumTrack(List<DrumNote> notes, double bpm)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            var events = new List<(int tick, byte status, byte note, byte velocity)>();
            double ticksPerSecond = (bpm / 60.0) * TicksPerQuarter;

            foreach (var n in notes)
            {
                int startTick = (int)Math.Round(n.StartSeconds * ticksPerSecond);
                int durTicks = Math.Max(10, (int)Math.Round(n.DurationSeconds * ticksPerSecond));

                byte note = (byte)Math.Clamp(n.MidiNote, 0, 127);
                byte vel = (byte)Math.Clamp(n.Velocity, 1, 127);

                events.Add((startTick, 0x99, note, vel));                  // Note On  (channel 10)
                events.Add((startTick + durTicks, 0x89, note, 0));         // Note Off (channel 10)
            }

            events.Sort((a, b) => a.tick.CompareTo(b.tick));

            int lastTick = 0;
            foreach (var (tick, status, note, velocity) in events)
            {
                WriteVarLen(bw, Math.Max(0, tick - lastTick));
                bw.Write(status);
                bw.Write(note);
                bw.Write(velocity);
                lastTick = tick;
            }

            WriteEndOfTrack(bw);
            return ms.ToArray();
        }

        private static void WriteEndOfTrack(BinaryWriter bw)
        {
            WriteVarLen(bw, 0);
            bw.Write((byte)0xFF); bw.Write((byte)0x2F); bw.Write((byte)0x00);
        }

        private static void WriteTrackChunk(BinaryWriter bw, byte[] data)
        {
            bw.Write(new[] { (byte)'M', (byte)'T', (byte)'r', (byte)'k' });
            WriteInt32BE(bw, data.Length);
            bw.Write(data);
        }

        private static void WriteVarLen(BinaryWriter bw, int value)
        {
            var bytes = new List<byte> { (byte)(value & 0x7F) };
            value >>= 7;
            while (value > 0)
            {
                bytes.Insert(0, (byte)((value & 0x7F) | 0x80));
                value >>= 7;
            }
            foreach (var b in bytes) bw.Write(b);
        }

        private static void WriteInt32BE(BinaryWriter bw, int value)
        {
            bw.Write((byte)((value >> 24) & 0xFF));
            bw.Write((byte)((value >> 16) & 0xFF));
            bw.Write((byte)((value >> 8) & 0xFF));
            bw.Write((byte)(value & 0xFF));
        }

        private static void WriteInt16BE(BinaryWriter bw, short value)
        {
            bw.Write((byte)((value >> 8) & 0xFF));
            bw.Write((byte)(value & 0xFF));
        }
    }
}
