# Redistribution policy

M0 contains no external conversion engines. Begin future integrations with an optional user-configured executable. Before distributing an engine, review the exact upstream build, linked components, data files, codecs, source availability and applicable license obligations. Record the version, flags, hash, URLs and notices for each platform.

| Tool | Upstream licensing | Proposed first integration |
| --- | --- | --- |
| FFmpeg | LGPL-2.1-or-later by default. Optional GPL components change obligations. Nonfree configurations can prohibit redistribution. | User-configured CLI. Evaluate a specifically audited LGPL build for optional distribution and review codec patents by market. [Upstream](https://ffmpeg.org/legal.html) |
| ImageMagick | ImageMagick License. Delegates and fonts carry their own terms. | Configured CLI with a restricted policy. Audit delegates and preserve license/notice texts before bundling. [Upstream](https://imagemagick.org/license/) |
| LibreOffice | MPL-2.0 distribution with multiple component licenses. | User-installed headless CLI with an isolated profile. Audit the installed component manifest before considering a bundle. [Upstream](https://www.libreoffice.org/licenses/) |
| qpdf | Apache-2.0 in current releases. | Configured CLI, then consider a separately reviewed package with upstream notices and dependency review. [Upstream](https://qpdf.readthedocs.io/en/stable/license.html) |
| Tesseract | Apache-2.0 for the engine. Language data and linked libraries require review. | Configured CLI with explicitly selected language packs. Review each data package and native dependency before distribution. [Upstream](https://github.com/tesseract-ocr/tesseract/blob/main/LICENSE) |
| 7-Zip | LGPL, BSD components and unRAR restrictions in applicable builds. | User-configured CLI. Audit exact variants and assess archive path traversal safeguards before extraction. [Upstream](https://www.7-zip.org/license.txt) |
| Ghostscript | AGPL or Artifex commercial license. | Defer. Assess integration and redistribution against Artifex's published terms, corresponding source and AGPL network provisions where applicable. Review whether an alternative meets each PDF use case. [Upstream](https://www.ghostscript.com/licensing/) |
| Calibre | GPLv3-family application with component-specific terms. | Evaluate a separately installed ebook-convert CLI. Review the chosen release, distributed source obligations and bundled components. Exclude DRM bypass from planned capabilities. [Upstream](https://calibre-ebook.com/about) |

Process separation alone does not settle license obligations. Review the shipped artifacts and integration method together. A public binary release must provide the application corresponding source and retain all third-party notices.
