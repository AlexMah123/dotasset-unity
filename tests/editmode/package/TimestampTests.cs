using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using NUnit.Framework;

namespace DotAsset.Tests
{
    // N11: export, report and log file names carry the time in UTC, the same as ExportedUtc inside them. Names
    // written before 0.12.0 carry local time and sort after new ones for a few hours east of UTC, so the newest log
    // is told by its write time, not by its name.
    public sealed class TimestampTests
    {
        const string Format = "yyyyMMdd-HHmmss";

        [Test]
        public void TheFileStampIsUtc()
        {
            string stamp = DotAssetPaths.Timestamp();
            DateTime parsed = DateTime.ParseExact(stamp, Format, CultureInfo.InvariantCulture);
            double minutes = Math.Abs((parsed - DateTime.UtcNow).TotalMinutes);
            Assert.Less(minutes, 1.0, "the file stamp " + stamp + " is " + Math.Round(minutes) + " minutes from UTC now " + DateTime.UtcNow.ToString(Format, CultureInfo.InvariantCulture));
        }

        [Test]
        public void ANewLogIsKeptWhenOlderLogsSortAfterIt()
        {
            // 200 old logs, the number kept, whose names sort after any name written today.
            Directory.CreateDirectory(DotAssetPaths.LogRoot);
            string later = DateTime.UtcNow.AddDays(1).ToString(Format, CultureInfo.InvariantCulture);
            var planted = new List<string>();
            try
            {
                for (int i = 0; i < 200; i++)
                {
                    string path = DotAssetPaths.Combine(DotAssetPaths.LogRoot, later + "_TimestampTest_" + i.ToString("D3", CultureInfo.InvariantCulture) + ".log");
                    File.WriteAllText(path, "planted\n");
                    File.SetLastWriteTimeUtc(path, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                    planted.Add(path);
                }
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
                RunResult r = RunDispatcher.Run("TimestampTestNoSuchRun", null, "", false);
                Assert.IsNotNull(r.LogFile, "the run wrote no log");
                Assert.IsTrue(File.Exists(r.LogFile), "the run's own log " + r.LogFile + " was deleted, the 200 older logs named after it were kept");
            }
            finally
            {
                foreach (string path in planted)
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
            }
        }
    }
}
