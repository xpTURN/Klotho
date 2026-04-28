using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using LiteNetLib;
using ZLogger;

namespace xpTURN.Klotho.LiteNetLib
{
    /// <summary>
    /// INetLogger → ILogger bridge adapter.
    /// </summary>
    public class LiteNetLibNetLogger : INetLogger
    {
        ILogger _logger = null;
        int _levelMask;

        public LiteNetLibNetLogger(ILogger logger, NetLogLevel[] levels = null )
        {
            _logger = logger;
            if (levels != null)
            {
                for (int i = 0; i < levels.Length; i++)
                    _levelMask |= 1 << (int)levels[i];
            }
        }

        public void WriteNet(NetLogLevel level, string str, params object[] args)
        {
            if (_levelMask != 0 && (_levelMask & (1 << (int)level)) == 0)
                return;

            string msg = args.Length > 0 ? string.Format(str, args) : str;
            msg = $"[LiteNetLib] {msg}";
            switch (level)
            {
                case NetLogLevel.Warning:
                    _logger?.ZLogWarning($"{msg}");
                    break;
                case NetLogLevel.Error:
                    _logger?.ZLogError($"{msg}");
                    break;
                case NetLogLevel.Trace:
                    _logger?.ZLogTrace($"{msg}");
                    break;
                case NetLogLevel.Info:
                default:
                    _logger?.ZLogInformation($"{msg}");
                    break;
            }
        }
    }
}