﻿using System;

namespace SharpMediaFoundationInterop
{
    public static class Log
    {
        public static bool WarnEnabled { get; set; } = true;
        public static void Warn(string message, Exception ex = null)
        {
            SinkWarn(message, ex);
        }

        public static bool ErrorEnabled { get; set; } = true;
        public static void Error(string message, Exception ex = null)
        {
            SinkError(message, ex);
        }

        public static bool TraceEnabled { get; set; } = true;
        public static void Trace(string message, Exception ex = null)
        {
            SinkTrace(message, ex);
        }

        public static bool DebugEnabled { get; set; }
#if DEBUG
            = true;
#endif

        public static void Debug(string message, Exception ex = null)
        {
            SinkDebug(message, ex);
        }

        public static bool InfoEnabled { get; set; }
#if DEBUG
            = true;
#endif
        public static void Info(string message, Exception ex = null)
        {
            SinkInfo(message, ex);
        }

        public static Action<string, Exception> SinkWarn = new Action<string, Exception>((m, ex) => { System.Diagnostics.Debug.WriteLine(m); });
        public static Action<string, Exception> SinkError = new Action<string, Exception>((m, ex) => { System.Diagnostics.Debug.WriteLine(m); });
        public static Action<string, Exception> SinkTrace = new Action<string, Exception>((m, ex) => { System.Diagnostics.Debug.WriteLine(m); });
        public static Action<string, Exception> SinkDebug = new Action<string, Exception>((m, ex) => { });
        public static Action<string, Exception> SinkInfo = new Action<string, Exception>((m, ex) => { });
    }
}
