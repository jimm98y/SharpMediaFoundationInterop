using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using static SharpMediaFoundationInterop.Utils.ObjC;

namespace SharpMediaFoundationInterop.Utils
{
    /// <summary>
    /// iOS's AVAudioSession: what an app's sound is routed by. A process starts in SoloAmbient, which the silent switch and
    /// the lock screen silence, and in which nothing records - so before AudioQueue plays, the session is moved to Playback,
    /// and before it records, to PlayAndRecord, the user asked for the microphone the first time. The devices are the
    /// session's: the outputs of its current route, and the inputs it has.
    /// </summary>
    [SupportedOSPlatform("ios14.0")]
    internal static unsafe class AudioSession
    {
        private static readonly IntPtr AVFAudio = LoadFramework("AVFAudio");
        private static readonly IntPtr AVFoundation = LoadFramework("AVFoundation");
        private static readonly object Lock = new object();
        private static bool _recording;

        // AVAudioSessionCategoryOptions: of PlayAndRecord, the speaker rather than the receiver, and Bluetooth headsets
        private const nint DefaultToSpeaker = 0x8;
        private const nint AllowBluetooth = 0x4;

        // AVAudioSessionRecordPermission, four character codes
        private const uint PermissionUndetermined = 0x756E6474; // 'undt'
        private const uint PermissionGranted = 0x67726E74;      // 'grnt'

        private static IntPtr Session => Send(Class("AVAudioSession"), "sharedInstance");

        private static IntPtr Category(string name) =>
            Constant(AVFAudio, name) is var value && value != IntPtr.Zero ? value : Constant(AVFoundation, name);

        /// <summary>
        /// Moves the session to Playback - or, to record, to PlayAndRecord, which it keeps once in it - and activates it.
        /// </summary>
        public static void Activate(bool record)
        {
            lock (Lock)
            {
                using var pool = AutoreleasePool.Create();
                _recording |= record;
                IntPtr error = IntPtr.Zero;
                bool set = _recording
                    ? SendBool(Session, "setCategory:withOptions:error:", Category("AVAudioSessionCategoryPlayAndRecord"), DefaultToSpeaker | AllowBluetooth, &error)
                    : SendBool(Session, "setCategory:error:", Category("AVAudioSessionCategoryPlayback"), &error);
                if (!set)
                    throw new InvalidOperationException($"The audio session's category cannot be set: {GetError(error)}");
                error = IntPtr.Zero;
                if (!SendBool(Session, "setActive:error:", (IntPtr)1, &error))
                    throw new InvalidOperationException($"The audio session cannot be activated: {GetError(error)}");
            }
        }

        /// <summary>
        /// Asks the user for the microphone where they have not been asked, and waits for their answer; throws
        /// <see cref="UnauthorizedAccessException"/> where they say no. The app's Info.plist must say why, in
        /// NSMicrophoneUsageDescription, or iOS ends the app as it asks.
        /// </summary>
        public static void RequestRecordPermission()
        {
            uint permission = (uint)SendNInt(Session, "recordPermission");
            if (permission == PermissionGranted)
                return;
            if (permission == PermissionUndetermined)
            {
                var answer = new Answer();
                var handle = GCHandle.Alloc(answer);
                // left, as the system may hold the block after it is called: once a process
                var block = CreateBlock((IntPtr)(delegate* unmanaged<BlockLiteral*, byte, void>)&OnAnswered, GCHandle.ToIntPtr(handle));
                SendVoid(Session, "requestRecordPermission:", (IntPtr)block);
                answer.Done.Wait();
                block->Context = IntPtr.Zero;
                handle.Free();
                if (answer.Granted)
                    return;
            }
            throw new UnauthorizedAccessException("The app may not use the microphone: allow it in Settings, Privacy & Security, Microphone");
        }

        private sealed class Answer
        {
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public volatile bool Granted;
        }

        [UnmanagedCallersOnly]
        private static void OnAnswered(BlockLiteral* block, byte granted)
        {
            if (block->Context != IntPtr.Zero && GCHandle.FromIntPtr(block->Context).Target is Answer answer)
            {
                answer.Granted = granted != 0;
                answer.Done.Set();
            }
        }

        /// <summary>The outputs of the session's current route: the speaker, headphones, a Bluetooth device - each its UID and name.</summary>
        public static (string Uid, string Name)[] Outputs()
        {
            using var pool = AutoreleasePool.Create();
            return Ports(Send(Send(Session, "currentRoute"), "outputs"));
        }

        /// <summary>The inputs the session can record from: each its UID and name. Of PlayAndRecord alone are they all listed.</summary>
        public static (string Uid, string Name)[] Inputs()
        {
            using var pool = AutoreleasePool.Create();
            return Ports(Send(Session, "availableInputs"));
        }

        private static (string, string)[] Ports(IntPtr array)
        {
            var ports = new List<(string, string)>();
            for (nint i = 0; i < Count(array); i++)
            {
                IntPtr port = ObjectAt(array, i);
                ports.Add((GetString(Send(port, "UID")), GetString(Send(port, "portName"))));
            }
            return ports.ToArray();
        }

        /// <summary>Records from the input of the UID given rather than the session's choice; false where there is none of it.</summary>
        public static bool SetPreferredInput(string uid)
        {
            using var pool = AutoreleasePool.Create();
            IntPtr inputs = Send(Session, "availableInputs");
            for (nint i = 0; i < Count(inputs); i++)
            {
                IntPtr port = ObjectAt(inputs, i);
                if (GetString(Send(port, "UID")) != uid)
                    continue;
                IntPtr error = IntPtr.Zero;
                return SendBool(Session, "setPreferredInput:error:", port, &error);
            }
            return false;
        }
    }
}
