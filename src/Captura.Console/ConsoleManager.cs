﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Captura.FFmpeg;
using Captura.Models;
using Captura.ViewModels;
using static System.Console;

namespace Captura
{
    // ReSharper disable once ClassNeverInstantiated.Global
    class ConsoleManager : IDisposable
    {
        readonly Settings _settings;
        readonly RecordingModel _recordingModel;
        readonly ScreenShotModel _screenShotModel;
        readonly IEnumerable<IVideoSourceProvider> _videoSourceProviders;
        readonly WebcamModel _webcamModel;
        readonly IPlatformServices _platformServices;
        readonly IMessageProvider _messageProvider;

        public ConsoleManager(Settings Settings,
            RecordingModel RecordingModel,
            ScreenShotModel ScreenShotModel,
            IEnumerable<IVideoSourceProvider> VideoSourceProviders,
            IPlatformServices PlatformServices,
            WebcamModel WebcamModel,
            IMessageProvider MessageProvider)
        {
            _settings = Settings;
            _recordingModel = RecordingModel;
            _screenShotModel = ScreenShotModel;
            _videoSourceProviders = VideoSourceProviders;
            _platformServices = PlatformServices;
            _webcamModel = WebcamModel;
            _messageProvider = MessageProvider;

            // Hide on Full Screen Screenshot doesn't work on Console
            Settings.UI.HideOnFullScreenShot = false;
        }

        public void Dispose()
        {
            ServiceProvider.Dispose();
        }

        public void CopySettings()
        {
            // Load settings dummy
            var dummySettings = new Settings(new FFmpegSettings());
            dummySettings.Load();

            _settings.WebcamOverlay = dummySettings.WebcamOverlay;
            _settings.MousePointerOverlay = dummySettings.MousePointerOverlay;
            _settings.Clicks = dummySettings.Clicks;
            _settings.Keystrokes = dummySettings.Keystrokes;
            _settings.Elapsed = dummySettings.Elapsed;

            // FFmpeg Path
            _settings.FFmpeg.FolderPath = dummySettings.FFmpeg.FolderPath;

            foreach (var overlay in dummySettings.Censored)
            {
                _settings.Censored.Add(overlay);
            }

            foreach (var overlay in dummySettings.TextOverlays)
            {
                _settings.TextOverlays.Add(overlay);
            }

            foreach (var overlay in dummySettings.ImageOverlays)
            {
                _settings.ImageOverlays.Add(overlay);
            }
        }

        public void Start(StartCmdOptions StartOptions)
        {
            _settings.IncludeCursor = StartOptions.Cursor;
            _settings.Clicks.Display = StartOptions.Clicks;
            _settings.Keystrokes.Display = StartOptions.Keys;

            if (File.Exists(StartOptions.FileName))
            {
                if (!StartOptions.Overwrite)
                {
                    if (!_messageProvider
                        .ShowYesNo("Output File Already Exists, Do you want to overwrite?", ""))
                        return;
                }

                File.Delete(StartOptions.FileName);
            }

            var videoSourceKind = HandleVideoSource(StartOptions);

            var videoWriter = HandleVideoEncoder(StartOptions, out var videoWriterKind);

            HandleAudioSource(StartOptions);

            HandleWebcam(StartOptions);

            if (StartOptions.FrameRate is int frameRate)
                _settings.Video.FrameRate = frameRate;

            if (StartOptions.AudioQuality is int aq)
                _settings.Audio.Quality = aq;

            if (StartOptions.VideoQuality is int vq)
                _settings.Video.Quality = vq;

            if (StartOptions.Delay > 0)
                Thread.Sleep(StartOptions.Delay);

            if (!_recordingModel.StartRecording(new RecordingModelParams
            {
                VideoSourceKind = videoSourceKind,
                VideoWriterKind = videoWriterKind,
                VideoWriter = videoWriter
            }, StartOptions.FileName))
                return;

            Task.Factory.StartNew(() =>
            {
                Loop(StartOptions);

                _recordingModel.StopRecording().Wait();

                Application.Exit();
            });

            // MouseKeyHook requires a Window Handle to register
            Application.Run(new ApplicationContext());
        }

        public void Shot(ShotCmdOptions ShotOptions)
        {
            _settings.IncludeCursor = ShotOptions.Cursor;

            // Screenshot Window with Transparency
            if (ShotOptions.Source != null && Regex.IsMatch(ShotOptions.Source, @"win:\d+"))
            {
                var ptr = int.Parse(ShotOptions.Source.Substring(4));

                try
                {
                    var win = _platformServices.GetWindow(new IntPtr(ptr));
                    var bmp = _screenShotModel.ScreenShotWindow(win);

                    _screenShotModel.SaveScreenShot(bmp, ShotOptions.FileName).Wait();
                }
                catch
                {
                    // Suppress Errors
                }
            }
            else
            {
                var videoSourceKind = HandleVideoSource(ShotOptions);

                var bmp = _screenShotModel.GetScreenShot(videoSourceKind).Result;

                _screenShotModel.SaveScreenShot(bmp, ShotOptions.FileName).Wait();
            }
        }

        IVideoSourceProvider HandleVideoSource(CommonCmdOptions CommonOptions)
        {
            var provider = _videoSourceProviders.FirstOrDefault(M => M.ParseCli(CommonOptions.Source));

            return provider;
        }

        void HandleAudioSource(StartCmdOptions StartOptions)
        {
            var audioSource = ServiceProvider.Get<AudioSource>();

            if (StartOptions.Microphone != -1 && StartOptions.Microphone < audioSource.AvailableRecordingSources.Count)
            {
                _settings.Audio.Enabled = true;
                audioSource.AvailableRecordingSources[StartOptions.Microphone].Active = true;
            }

            if (StartOptions.Speaker != -1 && StartOptions.Speaker < audioSource.AvailableLoopbackSources.Count)
            {
                _settings.Audio.Enabled = true;
                audioSource.AvailableLoopbackSources[StartOptions.Speaker].Active = true;
            }
        }

        IVideoWriterItem HandleVideoEncoder(StartCmdOptions StartOptions, out IVideoWriterProvider VideoWriterKind)
        {
            var ffmpegExists = FFmpegService.FFmpegExists;
            var sharpAviWriterProvider = ServiceProvider.Get<SharpAviWriterProvider>();

            // FFmpeg
            if (ffmpegExists && Regex.IsMatch(StartOptions.Encoder, @"^ffmpeg:\d+$"))
            {
                var index = int.Parse(StartOptions.Encoder.Substring(7));

                var ffmpegWriterProvider = ServiceProvider.Get<FFmpegWriterProvider>();
                var writers = ffmpegWriterProvider.ToArray();

                if (index < writers.Length)
                {
                    VideoWriterKind = ffmpegWriterProvider;

                    return writers[index];
                }
            }

            // SharpAvi
            else if (ServiceProvider.FileExists("SharpAvi.dll") && Regex.IsMatch(StartOptions.Encoder, @"^sharpavi:\d+$"))
            {
                var index = int.Parse(StartOptions.Encoder.Substring(9));

                var writers = sharpAviWriterProvider.ToArray();

                if (index < writers.Length)
                {
                    VideoWriterKind = sharpAviWriterProvider;

                    return writers[index];
                }
            }

            // Stream
            else if (ffmpegExists && Regex.IsMatch(StartOptions.Encoder, @"^stream:\S+$"))
            {
                var url = StartOptions.Encoder.Substring(7);
                _settings.FFmpeg.CustomStreamingUrl = url;

                VideoWriterKind = ServiceProvider.Get<StreamingWriterProvider>();

                return StreamingWriterProvider.GetCustomStreamingCodec();
            }

            // Rolling
            else if (ffmpegExists && Regex.IsMatch(StartOptions.Encoder, @"^roll:\d+$"))
            {
                var duration = int.Parse(StartOptions.Encoder.Substring(5));

                VideoWriterKind = ServiceProvider.Get<FFmpegWriterProvider>();

                return new FFmpegRollingWriterItem(duration);
            }

            VideoWriterKind = sharpAviWriterProvider;
            return sharpAviWriterProvider.First();
        }

        void HandleWebcam(StartCmdOptions StartOptions)
        {
            if (StartOptions.Webcam != -1 && StartOptions.Webcam < _webcamModel.AvailableCams.Count - 1)
            {
                _webcamModel.SelectedCam = _webcamModel.AvailableCams[StartOptions.Webcam + 1];

                // HACK: Sleep to prevent AccessViolationException
                Thread.Sleep(500);
            }
        }

        void Loop(StartCmdOptions StartOptions)
        {
            if (StartOptions.Length > 0)
            {
                var elapsed = 0;

                Write(TimeSpan.Zero);

                while (elapsed++ < StartOptions.Length)
                {
                    Thread.Sleep(1000);
                    Write(new string('\b', 8) + TimeSpan.FromSeconds(elapsed));
                }

                Write(new string('\b', 8));
            }
            else
            {
                const string recordingText = "Press p to pause or resume, q to quit";

                WriteLine(recordingText);

                char ReadChar()
                {
                    if (IsInputRedirected)
                    {
                        var line = ReadLine();

                        if (line != null && line.Length == 1)
                            return line[0];

                        return char.MinValue;
                    }

                    return char.ToLower(ReadKey(true).KeyChar);
                }

                char c;

                do
                {
                    c = ReadChar();

                    if (c != 'p')
                        continue;

                    _recordingModel.OnPauseExecute();

                    if (_recordingModel.RecorderState != RecorderState.Paused)
                    {
                        WriteLine("Resumed");
                    }
                } while (c != 'q');
            }
        }
    }
}