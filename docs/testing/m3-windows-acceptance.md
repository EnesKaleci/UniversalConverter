# M3 Windows acceptance

Use the Windows x64 unsigned development package. From the source commit run [the explicit setup script](../../.github/scripts/Install-MediaTools.ps1) with the extracted application directory as its argument. The script checks the pinned provider archive digest. The application uses the tools in its own media-tools directory. Verify the inner ZIP SHA-256 manifest. Record the Windows version, ZIP hash and PASS, FAIL or SKIP for each check.

| # | Check | Expected result |
|---|---|---|
| 1 | Launch | The application opens with tools absent. |
| 2 | Diagnostics | SQLite, Skia and HarfBuzz pass. |
| 3 | Install | The pinned archive and encoder inventory verify. |
| 4 | Tamper with the tool manifest or executable | The conversion fails before launch. |
| 5 | WAV → MP3 | A playable MP3 opens. |
| 6 | MP3 → WAV | The WAV contains stereo PCM. |
| 7 | WAV → FLAC | A readable FLAC opens. |
| 8 | FLAC → MP3 | The MP3 contains audio. |
| 9 | WAV → AAC | The AAC is an ADTS stream. |
| 10 | AAC → M4A | The result has an AAC stream in an MP4-family container. |
| 11 | WAV → OGG | The result contains Vorbis. |
| 12 | OGG → Opus | The result has Opus audio in Ogg. |
| 13 | MP4 → MKV | The target plays H.264 and AAC. |
| 14 | MKV → MP4 | The target plays H.264 and AAC. |
| 15 | MP4 → WebM | The target plays VP9 and Opus. |
| 16 | WebM → MP4 | The target plays H.264 and AAC. |
| 17 | MP4 → MOV | The target plays H.264 and AAC. |
| 18 | MOV → AVI | The target plays H.264 and MP3. |
| 19 | MP4 → MP3 | The result has audio only. |
| 20 | MP4 → WAV | The result contains PCM audio only. |
| 21 | MP4 → AAC/M4A | The selected audio format is produced. |
| 22 | MKV → MP3 | The result contains MP3 audio only. |
| 23 | MKV → FLAC | The result contains FLAC audio only. |
| 24 | WebM → Opus | The result contains Opus audio only. |
| 25 | Extract from silent video | The app reports a missing audio stream. |
| 26 | Convert silent video | The video output stays silent. |
| 27 | Custom audio bitrate | The selected bitrate changes the lossy encoder settings. |
| 28 | Choose video quality | The preset changes encoder options. |
| 29 | Choose 480p | The source is reduced when larger and is never upscaled. |
| 30 | Choose 30 fps | The generated stream reports about 30 fps. |
| 31 | Corrupt media | The job fails without a published file. |
| 32 | Collision | Rename preserves the existing file and Skip records a skip. |
| 33 | Cancel waiting job | The job ends without output. |
| 34 | Cancel running job | The process stops and temporary output is cleaned. |
| 35 | Unicode names | Sources and outputs retain Turkish characters. |
| 36 | Source hashes | Source checksums are unchanged. |
| 37 | Restart | History and existing preferences reload. |
| 38 | Change language | Settings, controls and errors update. |
| 39 | M2 regression | A real image and a simulation still work. |
| 40 | Close during conversion | Shutdown cancels work and leaves source files intact. |

## Media tool distribution

FFmpeg upstream lists Gyan Windows builds. This milestone uses Gyan Essentials snapshot 23c4540053 from 2026-11-11. The installer pins the release URL and SHA-256 and checks the configured encoders and build flags after an explicit invocation. Enable-gpl builds carry GPL obligations. H.264 and AAC may separately involve patent considerations depending on jurisdiction and use. The application ZIP excludes those executables pending package-specific review of corresponding source, dependency licenses and distribution rights. SignPath requires separately authorized upstream executables and product-specific review. The installer preserves the provider binaries and reports signature status.

The app uses the first usable video and first audio stream. It omits subtitles and metadata, and uses 48 kHz stereo. H.264 uses CRF 29/23/18 with medium preset. VP9 uses 38/32/25 and CPU-used 4. Custom sets audio bitrate and keeps standard video quality. AVI H.264 playback varies by player. AV1, HEVC, hardware acceleration and arbitrary stream selection are outside this implementation.

Sources: [FFmpeg downloads](https://ffmpeg.org/download.html), [FFmpeg license](https://ffmpeg.org/legal.html), [Gyan build](https://github.com/GyanD/codexffmpeg/releases/tag/2026-11-11-git-23c4540053), [SignPath terms](https://signpath.org/terms).
