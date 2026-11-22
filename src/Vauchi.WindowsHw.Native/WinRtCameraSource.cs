// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Security.Cryptography;

namespace Vauchi.WindowsHw.Native;

/// <summary>
/// The first colour camera through WinRT's frame reader, as grey frames.
/// What a frame means is <see cref="QrCamera"/>'s; this only makes the OS
/// calls.
/// </summary>
public sealed class WinRtCameraSource : ICameraSource
{
    /// <summary>
    /// Frames the decoder has not taken yet. A camera's frames are
    /// replaceable: an old one is dropped for a new one, never queued.
    /// </summary>
    private const int Waiting = 2;

    public async Task<ILumaFrames> OpenAsync(CancellationToken stop)
    {
        var groups = await MediaFrameSourceGroup.FindAllAsync().AsTask(stop).ConfigureAwait(false);
        var (group, colour) = groups
            .SelectMany(found => found.SourceInfos.Select(info => (found, info)))
            .FirstOrDefault(pair => pair.info.SourceKind == MediaFrameSourceKind.Color);
        if (group is null)
        {
            throw new HardwareFailureException(HardwareFailure.Unavailable);
        }
        var capture = new MediaCapture();
        var frames = new Frames(capture);
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group,
                // Shared: the display may show the same camera's preview (ADR-077 amendment 2026-11-01).
                SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                StreamingCaptureMode = StreamingCaptureMode.Video,
            }).AsTask(stop).ConfigureAwait(false);
            await frames.StartAsync(capture.FrameSources[colour.Id], stop).ConfigureAwait(false);
            return frames;
        }
        catch (Exception refused) when (refused is not (OperationCanceledException or HardwareFailureException))
        {
            await frames.DisposeAsync().ConfigureAwait(false);
            // E_ACCESSDENIED when the user or a policy turned the camera off for apps.
            throw new HardwareFailureException(OsFailures.From(refused.HResult));
        }
        catch
        {
            await frames.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Frames(MediaCapture capture) : ILumaFrames
    {
        private readonly Channel<LumaFrame> _frames = Channel.CreateBounded<LumaFrame>(
            new BoundedChannelOptions(Waiting) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        private MediaFrameReader? _reader;

        internal async Task StartAsync(MediaFrameSource source, CancellationToken stop)
        {
            _reader = await capture.CreateFrameReaderAsync(source).AsTask(stop).ConfigureAwait(false);
            _reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            _reader.FrameArrived += OnFrame;
            MediaFrameReaderStartStatus started = await _reader.StartAsync().AsTask(stop).ConfigureAwait(false);
            if (started != MediaFrameReaderStartStatus.Success)
            {
                throw new HardwareFailureException(started == MediaFrameReaderStartStatus.DeviceNotAvailable
                    ? HardwareFailure.Unavailable
                    : HardwareFailure.Failed);
            }
        }

        public async ValueTask<LumaFrame?> NextAsync(CancellationToken stop) =>
            await _frames.Reader.WaitToReadAsync(stop).ConfigureAwait(false) && _frames.Reader.TryRead(out LumaFrame? frame)
                ? frame
                : null;

        public async ValueTask DisposeAsync()
        {
            _frames.Writer.TryComplete();
            if (_reader is not null)
            {
                _reader.FrameArrived -= OnFrame;
                try
                {
                    await _reader.StopAsync().AsTask().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A camera that went away has nothing left to stop.
                }
                _reader.Dispose();
            }
            capture.Dispose();
        }

        private void OnFrame(MediaFrameReader sender, MediaFrameArrivedEventArgs arguments)
        {
            try
            {
                using MediaFrameReference? reference = sender.TryAcquireLatestFrame();
                SoftwareBitmap? bitmap = reference?.VideoMediaFrame?.SoftwareBitmap;
                if (bitmap is null
                    || bitmap.PixelWidth is <= 0 or > QrCamera.MaxSide
                    || bitmap.PixelHeight is <= 0 or > QrCamera.MaxSide)
                {
                    return;
                }
                using SoftwareBitmap grey = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Gray8);
                var buffer = new Windows.Storage.Streams.Buffer((uint)(grey.PixelWidth * grey.PixelHeight));
                grey.CopyToBuffer(buffer);
                CryptographicBuffer.CopyToByteArray(buffer, out byte[] pixels);
                _frames.Writer.TryWrite(new LumaFrame(grey.PixelWidth, grey.PixelHeight, pixels));
            }
            catch (Exception)
            {
                // A frame Windows could not hand over is a frame skipped; an exception here has no caller.
            }
        }
    }
}
