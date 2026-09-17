// Extension surface. The handler's ToState() returns the finished string, which is passed straight to the sink.
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace xpTURN.Klotho.Logging
{
    public static class KLoggerExtensions
    {
        // Runtime-level log: the level is chosen at the call site. Always emitted to IL.
        public static void KLog(this IKLogger logger, KLogLevel logLevel,
            [InterpolatedStringHandlerArgument("logger", "logLevel")] ref KLogHandler handler)
        { if (handler.Enabled) logger.Log(logLevel, handler.ToState(), null); }

        // Information / Warning / Error: gated at runtime by the handler's IsEnabled. Always emitted to IL.
        public static void KInformation(this IKLogger logger,
            [InterpolatedStringHandlerArgument("logger")] ref KLogHandlerInformation handler)
        { if (handler.Enabled) logger.Log(KLogLevel.Information, handler.ToState(), null); }

        public static void KWarning(this IKLogger logger,
            [InterpolatedStringHandlerArgument("logger")] ref KLogHandlerWarning handler)
        { if (handler.Enabled) logger.Log(KLogLevel.Warning, handler.ToState(), null); }

        // Two overloads: with and without an exception.
        public static void KError(this IKLogger logger,
            [InterpolatedStringHandlerArgument("logger")] ref KLogHandlerError handler)
        { if (handler.Enabled) logger.Log(KLogLevel.Error, handler.ToState(), null); }

        public static void KError(this IKLogger logger, Exception ex,
            [InterpolatedStringHandlerArgument("logger")] ref KLogHandlerError handler)
        { if (handler.Enabled) logger.Log(KLogLevel.Error, handler.ToState(), ex); }

        // Debug / Trace: runtime gate plus build-time removal. In release builds the call,
        // the compiler-generated handler, and its arguments are all stripped from IL.
        [Conditional("UNITY_EDITOR"), Conditional("DEBUG")]
        public static void KDebug(this IKLogger logger,
            [InterpolatedStringHandlerArgument("logger")] ref KLogHandlerDebug handler)
        { if (handler.Enabled) logger.Log(KLogLevel.Debug, handler.ToState(), null); }

        [Conditional("UNITY_EDITOR"), Conditional("DEBUG")]
        public static void KTrace(this IKLogger logger,
            [InterpolatedStringHandlerArgument("logger")] ref KLogHandlerTrace handler)
        { if (handler.Enabled) logger.Log(KLogLevel.Trace, handler.ToState(), null); }
    }
}
