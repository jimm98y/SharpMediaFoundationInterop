﻿using System;

namespace SharpMediaFoundation
{
    public interface IMediaTransform : IDisposable
    {
        bool ProcessInput(byte[] data, long timestamp);
        bool ProcessOutput(ref byte[] buffer, out uint length);
    }

    public interface IVideoTransform : IMediaTransform
    {
        uint OriginalWidth { get; }
        uint OriginalHeight { get; }
        uint Width { get; }
        uint Height { get; }
    }
}
