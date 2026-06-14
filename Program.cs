// =====================================================================
// DrumMidiGenerator
// ---------------------------------------------------------------------
// パイプライン:
//   1. FFmpeg        : MP3 -> WAV (44.1kHz / 16bit / stereo)
//   2. Demucs        : ドラムスステムを分離 (外部プロセス, Python)
//   3. C# 解析       : ドラムWAVをFFT解析してオンセット検出・楽器分類
//   4. MIDI出力      : Cakewalk SONARで読み込めるSMF Format1ファイルを生成
//
// 必要環境:
//   - .NET 8 SDK
//   - FFmpeg (ffmpeg.exe がPATH上にあること、または --ffmpeg で指定)
//   - Python + Demucs (pip install -U demucs)
//
// 使い方:
//   dotnet run -- "C:\path\to\song.mp3" 128
//   dotnet run -- "C:\path\to\song.mp3" 128 --sensitivity 0.8 --quantize 32
//   dotnet run -- "C:\path\to\song.mp3" 128 --ffmpeg "C:\tools\ffmpeg.exe" --keep-temp
//
// 出力:
//   入力MP3と同じフォルダに "<元ファイル名>_drums.mid" が生成されます。
// =====================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;

namespace DrumMidiGenerator
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("使い方: DrumMidiGenerator <input.mp3> [bpm] [options]");
                Console.WriteLine("  bpm              : 元曲のテンポ (省略時 128)");
                Console.WriteLine("  --ffmpeg path    : ffmpeg実行ファイルのパス (省略時 'ffmpeg')");
                Console.WriteLine("  --python path    : python実行ファイルのパス (省略時 'python')");
                Console.WriteLine("  --model name     : Demucsモデル名 (省略時 'htdemucs')");
                Console.WriteLine("  --sensitivity x  : オンセット検出感度 (省略時 1.0。小さいほど多く検出される)");
                Console.WriteLine("  --quantize n     : ノートをn分音符グリッドにスナップ (例: 16, 32。省略時スナップなし)");
                Console.WriteLine("  --output path    : 出力MIDIファイルのパス/ファイル名 (省略時 '<入力名>_drums.mid')");
                Console.WriteLine("  --keep-temp      : 作業用の中間ファイルを削除しない");
                return 1;
            }

            string inputMp3 = args[0];
            double bpm = 128.0;
            string ffmpegPath = "ffmpeg";
            string pythonPath = "python";
            string demucsModel = "htdemucs";
            double sensitivity = 1.0;
            int quantizeDivision = 0;
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
                    case "--sensitivity" when i + 1 < args.Length: sensitivity = double.Parse(args[++i]); break;
                    case "--quantize" when i + 1 < args.Length: quantizeDivision = int.Parse(args[++i]); break;
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

                // --- 3. 解析: オンセット検出 + 楽器分類 ---
                Console.WriteLine("[3/4] ドラムWAVを解析中...");
                var wav = WavFile.Load(drumsWavPath);
                var detector = new OnsetDetector(sensitivity);
                var onsets = detector.Detect(wav);

                int kicks = onsets.Count(o => o.MidiNote == DrumMap.Kick);
                int snares = onsets.Count(o => o.MidiNote == DrumMap.Snare);
                int hihats = onsets.Count(o => o.MidiNote == DrumMap.ClosedHiHat);
                Console.WriteLine($"  検出オンセット数: {onsets.Count}  (Kick:{kicks} / Snare:{snares} / HiHat:{hihats})");

                // --- 4. MIDI出力 ---
                Console.WriteLine("[4/4] MIDIファイルを生成中...");
                string midiPath = ResolveOutputPath(outputPath, inputDir, baseName);
                MidiWriter.Write(midiPath, onsets, bpm, quantizeDivision);
                Console.WriteLine($"完了: {midiPath}");

                if (!keepTemp)
                {
                    try { Directory.Delete(workDir, true); } catch { /* 無視 */ }
                }
                else
                {
                    Console.WriteLine($"中間ファイルを保持しました: {workDir}");
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"エラー: {ex.Message}");
                return 1;
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
                throw new Exception($"Pythonを起動できません ('{pythonPath}'). 'pip install -U demucs' でインストール済みか確認してください。詳細: {ex.Message}");
            }

            if (process == null)
                throw new Exception("Demucsプロセスの起動に失敗しました。");

            // Demucsの出力をリアルタイムでコンソールに表示しつつ、
            // 末尾の数行をエラーメッセージ用に保持する
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
    // WAVファイル読み込み (PCM 8/16/24/32bit -> モノラルfloat配列)
    // =====================================================================
    class WavFile
    {
        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public int BitsPerSample { get; private set; }
        public float[] Samples { get; private set; } = Array.Empty<float>();

        public static WavFile Load(string path)
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);

            if (new string(br.ReadChars(4)) != "RIFF")
                throw new InvalidDataException("RIFFファイルではありません。");
            br.ReadInt32(); // ファイルサイズ
            if (new string(br.ReadChars(4)) != "WAVE")
                throw new InvalidDataException("WAVEファイルではありません。");

            int sampleRate = 0, channels = 0, bitsPerSample = 0;
            byte[]? data = null;

            while (br.BaseStream.Position < br.BaseStream.Length)
            {
                if (br.BaseStream.Position + 8 > br.BaseStream.Length) break;

                string chunkId = new string(br.ReadChars(4));
                int chunkSize = br.ReadInt32();

                if (chunkId == "fmt ")
                {
                    br.ReadInt16(); // format tag
                    channels = br.ReadInt16();
                    sampleRate = br.ReadInt32();
                    br.ReadInt32(); // byte rate
                    br.ReadInt16(); // block align
                    bitsPerSample = br.ReadInt16();
                    int remaining = chunkSize - 16;
                    if (remaining > 0) br.ReadBytes(remaining);
                }
                else if (chunkId == "data")
                {
                    data = br.ReadBytes(chunkSize);
                }
                else
                {
                    br.ReadBytes(Math.Min(chunkSize, (int)(br.BaseStream.Length - br.BaseStream.Position)));
                }

                // チャンクは偶数バイト境界にパディングされる
                if (chunkSize % 2 != 0 && br.BaseStream.Position < br.BaseStream.Length)
                    br.ReadByte();
            }

            if (data == null) throw new InvalidDataException("dataチャンクが見つかりません。");

            return new WavFile
            {
                SampleRate = sampleRate,
                Channels = channels,
                BitsPerSample = bitsPerSample,
                Samples = ConvertToMonoFloat(data, bitsPerSample, channels)
            };
        }

        private static float[] ConvertToMonoFloat(byte[] data, int bits, int channels)
        {
            if (channels <= 0) channels = 1;
            int bytesPerSample = bits / 8;
            if (bytesPerSample <= 0) bytesPerSample = 2;

            int frameSize = bytesPerSample * channels;
            int frameCount = data.Length / frameSize;
            var result = new float[frameCount];

            for (int i = 0; i < frameCount; i++)
            {
                float sum = 0;
                for (int ch = 0; ch < channels; ch++)
                {
                    int offset = i * frameSize + ch * bytesPerSample;
                    float sample = bits switch
                    {
                        8 => (data[offset] - 128) / 128f,
                        16 => BitConverter.ToInt16(data, offset) / 32768f,
                        24 => Decode24(data, offset) / 8388608f,
                        32 => BitConverter.ToInt32(data, offset) / 2147483648f,
                        _ => 0f
                    };
                    sum += sample;
                }
                result[i] = sum / channels;
            }
            return result;
        }

        private static int Decode24(byte[] data, int offset)
        {
            int v = data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
            if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000); // 符号拡張
            return v;
        }
    }

    // =====================================================================
    // FFT (Cooley-Tukey, 2の累乗サイズ専用)
    // =====================================================================
    static class FFT
    {
        public static void Transform(Complex[] buf)
        {
            int n = buf.Length;
            if (n <= 1) return;

            // ビット反転並べ替え
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) (buf[i], buf[j]) = (buf[j], buf[i]);
            }

            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len;
                var wlen = new Complex(Math.Cos(ang), Math.Sin(ang));
                for (int i = 0; i < n; i += len)
                {
                    var w = Complex.One;
                    for (int j = 0; j < len / 2; j++)
                    {
                        var u = buf[i + j];
                        var v = buf[i + j + len / 2] * w;
                        buf[i + j] = u + v;
                        buf[i + j + len / 2] = u - v;
                        w *= wlen;
                    }
                }
            }
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
    }

    // =====================================================================
    // 検出されたドラムオンセット1件分
    // =====================================================================
    class Onset
    {
        public double TimeSeconds;
        public double Strength;
        public int MidiNote;
        public int Velocity;
    }

    // =====================================================================
    // オンセット検出 + 楽器分類 (マルチバンド方式)
    //
    // アルゴリズム概要:
    //  1. 全フレームのFFTマグニチュードを対数圧縮 log(1+|X|) してスペクトログラムを作成
    //     -> 振幅の小さい音も検出しやすくするための圧縮
    //  2. Kick/Snare/HiHatそれぞれの周波数帯ごとに、帯域限定スペクトラルフラックスを計算
    //     -> 1つの帯域が他帯域に埋もれて検出漏れするのを防ぐ (ポリフォニック検出)
    //  3. 各帯域で適応的しきい値によるピーク検出
    //  4. ピーク検出されたフレームについて、そのフレーム内で対象帯域のエネルギー比が
    //     一定以上かを確認 (クロスバンド確認チェック)
    //     -> 他楽器の過渡音が漏れ込んで誤検出するのを抑制
    //  5. 楽器ごとにフラックス強度を正規化してベロシティを算出
    //
    // 注意: ヒューリスティック(経験則)による分類です。クラッシュ/ライド/タムの区別、
    //       オープン/クローズハイハットの判定などは含まれていません。
    //       下記の定数を調整してチューニングしてください。
    // =====================================================================
    class OnsetDetector
    {
        private const int WindowSize = 2048; // FFTサイズ
        private const int HopSize = 512;     // フレーム間隔

        // ピーク検出パラメータ
        private const int PeakSpread = 2;         // 局所最大値判定の幅 (フレーム数)
        private const int AdaptiveAvgWindow = 12; // 適応的しきい値の移動平均幅

        // 周波数帯の境界 (Hz)
        private const double KickLoHz = 20, KickHiHz = 150;
        private const double SnareLoHz = 150, SnareHiHz = 2500;
        private const double HiHatLoHz = 2500, HiHatHiHz = 12000;

        // 帯域ごとのしきい値の倍率 (大きいほど検出されにくくなる)
        private const double KickThresholdMul = 1.5;
        private const double SnareThresholdMul = 1.6;
        private const double HiHatThresholdMul = 1.8;

        // 帯域ごとの最小オンセット間隔 (秒)
        private const double KickMinGap = 0.08;
        private const double SnareMinGap = 0.06;
        private const double HiHatMinGap = 0.04;

        // クロスバンド確認チェック: そのフレームで対象帯域のエネルギー比が
        // この値未満なら誤検出として捨てる (0.0〜1.0)
        private const double KickRatioMin = 0.35;
        private const double SnareRatioMin = 0.20;
        private const double HiHatRatioMin = 0.30;

        // ベロシティの最小値・最大値
        private const int VelocityMin = 50;
        private const int VelocityRange = 77; // VelocityMin + VelocityRange = 127

        private readonly double _sensitivity;

        /// <param name="sensitivity">検出感度の倍率。1.0が標準。小さいほど多く検出される。</param>
        public OnsetDetector(double sensitivity = 1.0)
        {
            _sensitivity = sensitivity <= 0 ? 1.0 : sensitivity;
        }

        public List<Onset> Detect(WavFile wav)
        {
            float[] samples = wav.Samples;
            int sr = wav.SampleRate;
            int numFrames = (samples.Length - WindowSize) / HopSize + 1;
            if (numFrames <= 0) return new List<Onset>();

            // --- スペクトログラム (対数圧縮マグニチュード) を作成 ---
            double[] window = HannWindow(WindowSize);
            var logMag = new double[numFrames][];

            for (int f = 0; f < numFrames; f++)
            {
                int start = f * HopSize;
                var buf = new Complex[WindowSize];
                for (int i = 0; i < WindowSize; i++)
                {
                    int idx = start + i;
                    double s = idx < samples.Length ? samples[idx] : 0.0;
                    buf[i] = new Complex(s * window[i], 0);
                }
                FFT.Transform(buf);

                var lm = new double[WindowSize / 2];
                for (int i = 0; i < lm.Length; i++)
                    lm[i] = Math.Log(1.0 + buf[i].Magnitude);
                logMag[f] = lm;
            }

            int numBins = logMag[0].Length;
            double binHz = (double)sr / WindowSize;
            int B(double hz) => Math.Clamp((int)(hz / binHz), 0, numBins - 1);

            int kickLo = B(KickLoHz), kickHi = B(KickHiHz);
            int snareLo = B(SnareLoHz), snareHi = B(SnareHiHz);
            int hihatLo = B(HiHatLoHz), hihatHi = B(HiHatHiHz);

            // クロスバンド比較に使う全体範囲 (打楽器が主に存在する帯域)
            int totalLo = kickLo, totalHi = hihatHi;

            var kickOnsets = DetectBand(logMag, numFrames, sr, kickLo, kickHi,
                KickThresholdMul, KickMinGap, DrumMap.Kick,
                f => BandRatio(logMag[f], kickLo, kickHi, totalLo, totalHi) >= KickRatioMin);

            var snareOnsets = DetectBand(logMag, numFrames, sr, snareLo, snareHi,
                SnareThresholdMul, SnareMinGap, DrumMap.Snare,
                f => BandRatio(logMag[f], snareLo, snareHi, totalLo, totalHi) >= SnareRatioMin);

            var hihatOnsets = DetectBand(logMag, numFrames, sr, hihatLo, hihatHi,
                HiHatThresholdMul, HiHatMinGap, DrumMap.ClosedHiHat,
                f => BandRatio(logMag[f], hihatLo, hihatHi, totalLo, totalHi) >= HiHatRatioMin);

            // 楽器ごとにベロシティを正規化 (キックとハイハットでエネルギー量のスケールが異なるため)
            NormalizeVelocity(kickOnsets);
            NormalizeVelocity(snareOnsets);
            NormalizeVelocity(hihatOnsets);

            var all = new List<Onset>();
            all.AddRange(kickOnsets);
            all.AddRange(snareOnsets);
            all.AddRange(hihatOnsets);
            all.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
            return all;
        }

        /// <summary>
        /// 指定した周波数帯について帯域限定スペクトラルフラックスを計算し、
        /// 適応的しきい値でピーク検出を行う。confirmCheckで誤検出を除外する。
        /// </summary>
        private List<Onset> DetectBand(
            double[][] logMag, int numFrames, int sr,
            int bandLo, int bandHi, double thresholdMul, double minGapSec, int midiNote,
            Func<int, bool> confirmCheck)
        {
            var result = new List<Onset>();

            var flux = new double[numFrames];
            for (int f = 1; f < numFrames; f++)
            {
                double sum = 0;
                var prev = logMag[f - 1];
                var cur = logMag[f];
                for (int i = bandLo; i <= bandHi; i++)
                {
                    double diff = cur[i] - prev[i];
                    if (diff > 0) sum += diff;
                }
                flux[f] = sum;
            }

            double maxFlux = flux.Max();
            if (maxFlux <= 0) return result;

            double lastTime = -1;
            for (int f = PeakSpread; f < numFrames - PeakSpread; f++)
            {
                bool isPeak = true;
                for (int k = -PeakSpread; k <= PeakSpread; k++)
                {
                    if (k != 0 && flux[f] < flux[f + k]) { isPeak = false; break; }
                }
                if (!isPeak) continue;

                int lo = Math.Max(0, f - AdaptiveAvgWindow);
                int hi = Math.Min(numFrames - 1, f + AdaptiveAvgWindow);
                double localAvg = 0;
                for (int k = lo; k <= hi; k++) localAvg += flux[k];
                localAvg /= (hi - lo + 1);

                double threshold = localAvg * thresholdMul * _sensitivity + maxFlux * 0.01;
                if (flux[f] <= threshold) continue;

                double time = (double)(f * HopSize) / sr;
                if (lastTime >= 0 && (time - lastTime) < minGapSec) continue;

                if (!confirmCheck(f)) continue;

                result.Add(new Onset
                {
                    TimeSeconds = time,
                    Strength = flux[f],
                    MidiNote = midiNote,
                    Velocity = 0
                });
                lastTime = time;
            }

            return result;
        }

        /// <summary>
        /// フレーム内で [lo,hi] の帯域が、[totalLo,totalHi] の全体範囲に対して
        /// どれだけの割合を占めているかを返す (0.0〜1.0)。
        /// </summary>
        private static double BandRatio(double[] frame, int lo, int hi, int totalLo, int totalHi)
        {
            double bandSum = 0, totalSum = 1e-9;
            for (int i = totalLo; i <= totalHi; i++) totalSum += frame[i];
            for (int i = lo; i <= hi; i++) bandSum += frame[i];
            return bandSum / totalSum;
        }

        private static void NormalizeVelocity(List<Onset> onsets)
        {
            if (onsets.Count == 0) return;
            double max = onsets.Max(o => o.Strength);
            if (max <= 0) max = 1;
            foreach (var o in onsets)
                o.Velocity = (int)Math.Clamp(VelocityMin + (o.Strength / max) * VelocityRange, 1, 127);
        }

        private static double[] HannWindow(int size)
        {
            var w = new double[size];
            for (int i = 0; i < size; i++)
                w[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (size - 1)));
            return w;
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
        private const int NoteLengthTicks = 60; // 32分音符程度の長さ

        /// <param name="quantizeDivision">
        /// 0以下の場合はスナップなし。8/16/32などを指定すると、その分音符グリッドに
        /// オンセット時間をスナップする (例: 16 -> 16分音符単位)。
        /// </param>
        public static void Write(string path, List<Onset> onsets, double bpm, int quantizeDivision = 0)
        {
            using var fs = new FileStream(path, FileMode.Create);
            using var bw = new BinaryWriter(fs);

            WriteHeader(bw, format: 1, numTracks: 2, division: TicksPerQuarter);

            var tempoTrack = BuildTempoTrack(bpm);
            WriteTrackChunk(bw, tempoTrack);

            var drumTrack = BuildDrumTrack(onsets, bpm, quantizeDivision);
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

        private static byte[] BuildDrumTrack(List<Onset> onsets, double bpm, int quantizeDivision)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            var events = new List<(int tick, byte status, byte note, byte velocity)>();
            double ticksPerSecond = (bpm / 60.0) * TicksPerQuarter;

            // quantizeDivision (例: 16, 32) からグリッド幅(tick)を算出
            int gridTicks = quantizeDivision > 0 ? Math.Max(1, (TicksPerQuarter * 4) / quantizeDivision) : 0;

            foreach (var o in onsets)
            {
                int startTick = (int)Math.Round(o.TimeSeconds * ticksPerSecond);
                if (gridTicks > 0)
                    startTick = (int)Math.Round((double)startTick / gridTicks) * gridTicks;

                byte note = (byte)o.MidiNote;
                byte vel = (byte)Math.Clamp(o.Velocity, 1, 127);

                events.Add((startTick, 0x99, note, vel));               // Note On  (channel 10)
                events.Add((startTick + NoteLengthTicks, 0x89, note, 0)); // Note Off (channel 10)
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
