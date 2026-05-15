using System.Collections.Frozen;
using System.IO;
using System.Text;

namespace AppUserDataScanner.Detection;

internal abstract class BaseDetector : IDetector
{
    public abstract string Label { get; }
    public abstract FrozenDictionary<string, (int Score, bool isDirectory)> Scores { get; }

    private const string DirSuffix = "(dir), ";
    private const string FileSuffix = "(file), ";

    public virtual DetectionResult Evaluate(string directoryPath)
    {
        int score = 0;
        StringBuilder signals = Program.StringBuilderObjectPool.Get();
        try
        {
            foreach (var kv in Scores)
            {
                string targetPath = Path.Combine(directoryPath, kv.Key);
                bool exists = kv.Value.isDirectory ? Directory.Exists(targetPath) : File.Exists(targetPath);

                if (exists)
                {
                    score += kv.Value.Score;
                    signals.Append(kv.Key).Append(kv.Value.isDirectory ? DirSuffix : FileSuffix);
                }
            }

            if (score == 0)
            {
                return DetectionResult.NoMatch;
            }
            else
            {
                signals.Remove(signals.Length - 2, 2); // Remove last ", "
            }

            return new DetectionResult
            {
                Score = score,
                MatchedSignals = signals.ToString()
            };
        }
        finally
        {
            Program.StringBuilderObjectPool.Return(signals);
        }
    }
}