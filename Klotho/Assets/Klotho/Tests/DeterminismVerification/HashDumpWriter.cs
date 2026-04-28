using System;
using System.IO;

namespace xpTURN.Klotho.DeterminismVerification
{
    public sealed class HashDumpWriter : IDisposable
    {
        private readonly StreamWriter _writer;

        public HashDumpWriter(string filePath)
        {
            _writer = new StreamWriter(filePath, false, System.Text.Encoding.UTF8);
            _writer.WriteLine("tick,hash");
        }

        public void WriteHash(int tick, long hash)
        {
            _writer.Write(tick);
            _writer.Write(',');
            _writer.WriteLine(hash);
        }

        public void Dispose()
        {
            _writer.Flush();
            _writer.Dispose();
        }
    }
}
