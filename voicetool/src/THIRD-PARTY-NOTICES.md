# Third-party notices

VoiceTool is built from the open-source software and open models listed here. Thank you to everyone who made them.
Each one keeps its own license, and nothing in VoiceTool changes the rights those licenses give you.

Setup downloads these pieces straight from their official sources (Python Software Foundation, PyPI, the llama.cpp
project on GitHub, and Hugging Face) and checks every download against a SHA-256 fingerprint that is written into Setup
before the file is used: the Python installer, every Python package (pip's hash-checking mode, `--require-hashes`, which
refuses any file that doesn't match), the llama.cpp programs and the model files. The one package that PyPI has no ready-made
file for (proxy_tools) comes in VoiceTool's own `wheels` folder and is checked the same way (see section 6). Every Python
package keeps its own license files in its `*.dist-info` folder under `.venv\Lib\site-packages`.

## Contents

1. Speech and language models
2. The program that runs the tidy-up model
3. Python and the packages VoiceTool uses directly
4. Packages those need (installed automatically)
5. Windows components
6. Special notes: LGPL libraries, MPL-2.0 libraries, CC BY 4.0 attribution
7. License texts

## 1. Speech and language models

| Component | License | Copyright and credit | Link |
|---|---|---|---|
| **NVIDIA Parakeet TDT 0.6B v2** (speech recognition) | CC BY 4.0 | Copyright NVIDIA Corporation. Created by NVIDIA. Converted to ONNX and quantized to int8 by Ilya Stupakov (istupakov) for the onnx-asr project. These are changes to the original; the files are otherwise used as converted. | Original: https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2 . ONNX conversion: https://huggingface.co/istupakov/parakeet-tdt-0.6b-v2-onnx . License: https://creativecommons.org/licenses/by/4.0/ |
| **Qwen3-4B-Instruct-2507** (tidy-up and commands), file `Qwen3-4B-Instruct-2507-Q4_K_M.gguf` | Apache-2.0 | Copyright 2024 Alibaba Cloud (Qwen team). This is a quantized (Q4_K_M, GGUF format) version of the original model, which is a change to the original. | Original: https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507 . The quantized file is downloaded from the Unsloth repository https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF (revision `a06e946bb6b655725eafa393f4a9745d460374c9`). |

Where the Qwen file comes from: Qwen's own Hugging Face account does not publish a GGUF file for this model. The Unsloth
repository is the best-known source, and it is the same file, byte for byte (SHA-256
`3605803b982cb64aead44f6c1b2ae36e3acdb41d8e46c8a94c6533bc4c67e597`), as the one VoiceTool was developed and tested with.
Unsloth's repository declares Apache-2.0, like the original.

The speech model files are part of the VoiceTool download. The tidy-up model is downloaded by Setup, directly from
Hugging Face to your computer. Parakeet and Qwen are used under their licenses; neither NVIDIA, Alibaba Cloud, Unsloth nor
istupakov endorses VoiceTool.

## 2. The program that runs the tidy-up model

| Component | License | Copyright | Link |
|---|---|---|---|
| **llama.cpp**, release b11388 (`llama-server.exe` and its libraries, Vulkan and CPU builds for Windows) | MIT | Copyright (c) 2023-2026 The ggml authors | https://github.com/ggml-org/llama.cpp |
| **LLVM OpenMP runtime** (`libomp.dll`, shipped inside the llama.cpp release) | Apache-2.0 with LLVM Exceptions | Copyright the LLVM Project contributors | https://openmp.llvm.org/ and https://llvm.org/LICENSE.txt |
| **cpp-httplib** (the web server inside `llama-server`; built into the release's `llama-server-impl.dll` and `llama-common.dll`) | MIT | Copyright (c) 2017 yhirose | https://github.com/yhirose/cpp-httplib |
| **nlohmann/json** (JSON for C++; the llama.cpp release calls it "jsonhpp"; built into `llama-common.dll`) | MIT | Copyright (c) 2013-2025 Niels Lohmann | https://github.com/nlohmann/json |
| **BoringSSL** (the encryption library cpp-httplib is built with; built into the same files; VoiceTool's local server never uses it, because it listens only on 127.0.0.1 without encryption) | Apache-2.0 (the Go Authors' BSD-3-Clause text that comes with its license file covers test code that is not part of the build) | Copyright Google LLC and the BoringSSL authors | https://boringssl.googlesource.com/boringssl |
| **stb_image** (image reading in `mtmd.dll`, which VoiceTool does not use for pictures) | Public domain or MIT, at your choice | Sean Barrett and contributors | https://github.com/nothings/stb |
| **subprocess.h** (starting programs; in `llama-common.dll` and `mtmd.dll`) | Unlicense (public domain) | Neil Henning (sheredom) | https://github.com/sheredom/subprocess.h |

How this list was made: the cached release zips for b11388 (Vulkan and CPU, both checked against the fingerprints in
`fetch_models.py`) were read without being run. Besides `LICENSE-LLVM-OpenMP`, they contain no license files, but
`llama.exe` has the notices for llama.cpp, nlohmann/json ("jsonhpp"), BoringSSL, cpp-httplib and the LLVM OpenMP runtime
built into it, and the program files show the code of cpp-httplib, nlohmann/json, subprocess.h and stb_image inside them.
`ggml-vulkan.dll` also carries Khronos Group's Vulkan and SPIR-V definitions (Apache-2.0 or MIT). The MIT and Apache-2.0
license texts for all of these are in section 7; the copyright lines are in the table above. The release zip also carries
the LLVM OpenMP license (`LICENSE-LLVM-OpenMP`), which ends up in `runtime\llama-vulkan` and `runtime\llama-cpu`.
The Vulkan build uses your graphics card's own Vulkan driver, which is not part of VoiceTool.

## 3. Python and the packages VoiceTool uses directly

| Component | Version | License | Copyright | Link |
|---|---|---|---|---|
| **Python** (installed by Setup only if your computer has no Python 3.13; includes Tcl/Tk, OpenSSL, SQLite, zlib, expat and others, each with its own license in Python's `LICENSE.txt`) | 3.13 | PSF License | Copyright (c) 2001 and later Python Software Foundation. All rights reserved. | https://www.python.org/ and https://docs.python.org/3/license.html |
| azure-identity | 1.26.0 | MIT | Copyright (c) Microsoft Corporation | https://github.com/Azure/azure-sdk-for-python |
| numpy | 2.5.4 | BSD-3-Clause (the wheel also carries 0BSD, MIT, Zlib and CC0-1.0 parts, including OpenBLAS; their notices are in numpy's `licenses` folder) | Copyright (c) 2005-2025, NumPy Developers | https://numpy.org |
| onnx-asr | 0.12.0 | MIT | Copyright (c) 2025 Ilya Stupakov | https://github.com/istupakov/onnx-asr |
| onnxruntime | 1.31.0 | MIT (its `ThirdPartyNotices.txt`, inside the package, lists the code it includes) | Copyright (c) Microsoft Corporation | https://onnxruntime.ai |
| openai | 3.36.0 | Apache-2.0 | Copyright OpenAI | https://github.com/openai/openai-python |
| Pillow | 13.0.0 | MIT-CMU (HPND-style) | Copyright (c) 1997-2011 Secret Labs AB; (c) 1995-2011 Fredrik Lundh and contributors; (c) 2010 Jeffrey A. Clark and contributors | https://python-pillow.github.io |
| pycaw | 20260927 | MIT | Copyright (c) 2016 AndreMiras | https://github.com/AndreMiras/pycaw |
| **pynput** | 1.8.2 | **LGPL-3.0** | Copyright (C) Moses Palmer | https://github.com/moses-palmer/pynput |
| **pystray** | 0.19.5 | **LGPL-3.0** | Copyright (C) Moses Palmer | https://github.com/moses-palmer/pystray |
| python-docx | 1.2.0 | MIT | Copyright (c) 2013 Steve Canny | https://github.com/python-openxml/python-docx |
| pywebview | 6.2.1 | BSD-3-Clause | Copyright (c) 2014-2017, Roman Sirokov | https://pywebview.flowrl.com |
| pywin32 | 312 | PSF License | Copyright Mark Hammond and contributors | https://github.com/mhammond/pywin32 |
| SoundCard | 0.4.6 | BSD-3-Clause | Copyright (c) 2016 Bastian Bechtold | https://github.com/bastibe/SoundCard |
| sounddevice | 0.5.6 | MIT (bundles PortAudio, which has an MIT-style license) | Copyright (c) 2015-2025 Matthias Geier. PortAudio: Copyright (c) 1999-2011 Ross Bencina and Phil Burk | https://python-sounddevice.readthedocs.io and http://www.portaudio.com |
| pip (inside VoiceTool's private environment) | as installed | MIT, with its own vendored libraries | Copyright (c) The pip developers | https://pip.pypa.io |

The ASIO build of PortAudio that sounddevice carries is deleted by Setup and is not used.

## 4. Packages those need (installed automatically)

| Package | Version | License | Copyright | Link |
|---|---|---|---|---|
| annotated-types | 0.8.0 | MIT | Copyright (c) 2022 the contributors | https://github.com/annotated-types/annotated-types |
| anyio | 4.15.1 | MIT | Copyright (c) 2018 Alex Gronholm | https://github.com/agronholm/anyio |
| azure-core | 1.41.0 | MIT | Copyright (c) Microsoft Corporation | https://github.com/Azure/azure-sdk-for-python |
| bottle | 0.13.4 | MIT | Copyright (c) 2009-2024, Marcel Hellkamp | https://github.com/bottlepy/bottle |
| **certifi** | 2026.10.19 | **MPL-2.0** | Copyright Kenneth Reitz and the certifi contributors | https://github.com/certifi/python-certifi |
| cffi | 2.1.1 | MIT-0 | Copyright Armin Rigo, Maciej Fijalkowski | https://github.com/python-cffi/cffi |
| charset-normalizer | 3.5.2 | MIT | Copyright (c) 2025 TAHRI Ahmed R. | https://github.com/jawah/charset_normalizer |
| clr_loader | 0.3.1 | MIT | Copyright (c) 2019-2026 Benedikt Reinartz | https://github.com/pythonnet/clr-loader |
| comtypes | 1.4.17 | MIT | Copyright (c) 2006-2013, Thomas Heller; (c) 2014, Comtypes Developers | https://github.com/enthought/comtypes |
| cryptography | 50.0.2 | Apache-2.0 OR BSD-3-Clause (used under Apache-2.0) | Copyright The Python Cryptographic Authority and individual contributors | https://github.com/pyca/cryptography |
| flatbuffers | 25.12.19 | Apache-2.0 | Copyright Google Inc. | https://github.com/google/flatbuffers |
| h11 | 0.16.0 | MIT | Copyright (c) 2016 Nathaniel J. Smith and other contributors | https://github.com/python-hyper/h11 |
| httpcore2 | 2.16.0 | BSD-3-Clause | Copyright 2026 to present Pydantic Services Inc. and individual contributors; (c) 2020 Encode OSS Ltd. | https://github.com/pydantic/httpx2 |
| httpx2 | 2.16.0 | BSD-3-Clause | Copyright 2026 to present Pydantic Services Inc. and individual contributors; (c) 2019 Encode OSS Ltd. | https://github.com/pydantic/httpx2 |
| idna | 3.20 | BSD-3-Clause | Copyright (c) 2013-2026, Kim Davies and contributors | https://github.com/kjd/idna |
| jiter | 0.17.0 | MIT | Copyright (c) 2022 to present Samuel Colvin | https://github.com/pydantic/jiter |
| lxml | 6.1.3 | BSD-3-Clause | Copyright (c) 2004 Infrae. All rights reserved. | https://github.com/lxml/lxml |
| msal | 1.39.0 | MIT | Copyright (c) Microsoft Corporation | https://github.com/AzureAD/microsoft-authentication-library-for-python |
| msal-extensions | 1.3.1 | MIT | Copyright (c) Microsoft Corporation | https://github.com/AzureAD/microsoft-authentication-extensions-for-python |
| packaging | 26.3 | Apache-2.0 OR BSD-2-Clause (used under Apache-2.0) | Copyright Donald Stufft and individual contributors | https://github.com/pypa/packaging |
| protobuf | 7.36.2 | BSD-3-Clause | Copyright 2008 Google Inc. All rights reserved. | https://github.com/protocolbuffers/protobuf |
| proxy_tools | 0.1.0 | MIT | Copyright Jonathan Tushman | https://github.com/jtushman/proxy_tools |
| psutil | 7.2.2 | BSD-3-Clause | Copyright (c) 2009, Jay Loden, Dave Daeschler, Giampaolo Rodola | https://github.com/giampaolo/psutil |
| pycparser | 3.11 | BSD-3-Clause | Copyright (c) 2008 and later, Eli Bendersky | https://github.com/eliben/pycparser |
| pydantic | 2.14.1 | MIT | Copyright (c) 2017 to present Pydantic Services Inc. and individual contributors | https://github.com/pydantic/pydantic |
| pydantic_core | 2.50.1 | MIT | Copyright (c) 2022 Samuel Colvin | https://github.com/pydantic/pydantic-core |
| PyJWT | 2.15.1 | MIT | Copyright (c) 2015-2022 Jose Padilla | https://github.com/jpadilla/pyjwt |
| pythonnet | 3.2.1 | MIT | Copyright (c) 2006-2021 the contributors of the Python.NET project | https://pythonnet.github.io |
| requests | 2.34.2 | Apache-2.0 | Copyright 2019 Kenneth Reitz (the project's NOTICE file: "Requests, Copyright 2019 Kenneth Reitz") | https://github.com/psf/requests |
| six | 1.17.0 | MIT | Copyright (c) 2010-2024 Benjamin Peterson | https://github.com/benjaminp/six |
| sniffio | 1.3.1 | MIT OR Apache-2.0 (used under MIT) | Copyright Nathaniel J. Smith | https://github.com/python-trio/sniffio |
| truststore | 0.10.5 | MIT | Copyright (c) 2022 Seth Michael Larson | https://github.com/sethmlarson/truststore |
| typing-inspection | 0.4.4 | MIT | Copyright (c) Pydantic Services Inc. 2025 to present | https://github.com/pydantic/typing-inspection |
| typing_extensions | 4.16.0 | PSF-2.0 | Copyright (c) 2001 and later Python Software Foundation | https://github.com/python/typing_extensions |
| urllib3 | 2.8.0 | MIT | Copyright (c) 2008-2020 Andrey Petrov and contributors | https://github.com/urllib3/urllib3 |

## 5. Windows components

- **Microsoft Edge WebView2 Runtime.** The VoiceTool window is drawn by the WebView2 Runtime, which is provided by
  Windows (it is part of Windows 11 and of almost all Windows 10 computers). VoiceTool does not include or redistribute
  it. It is Microsoft software under Microsoft's own license terms: https://developer.microsoft.com/microsoft-edge/webview2/
  pywebview's helper libraries for it (`Microsoft.Web.WebView2.Core.dll` and `Microsoft.Web.WebView2.WinForms.dll`) are
  Microsoft's, used under Microsoft's license for the WebView2 SDK.
- **Microsoft Visual C++ Redistributable (2015-2022, x64).** The speech model's runtime (onnxruntime) and the tidy-up
  program (llama.cpp) are built with Microsoft's compiler and need this runtime. VoiceTool does not include or
  redistribute it: if the computer doesn't already have a recent one, Setup downloads Microsoft's installer
  (https://aka.ms/vs/17/release/vc_redist.x64.exe) directly from Microsoft, checks that it carries Microsoft's digital
  signature, and runs it (Windows asks for permission once). It is Microsoft software under Microsoft's own license terms,
  and Uninstall leaves it in place because other programs share it.
- **Windows speech (SAPI) and the Windows audio, clipboard and keyboard services** are parts of Windows and are used
  through its normal programming interfaces.

## 6. Special notes

### pynput and pystray are LGPL-3.0 libraries (you can swap them)

VoiceTool uses **pynput** (keyboard shortcuts) and **pystray** (the tray icon), both licensed under the GNU Lesser
General Public License, version 3. VoiceTool is a separate program that uses them as ordinary, unmodified Python
packages. They are installed as plain Python source files, not packed inside anything, so you can change or replace them:

- Where they are: `.venv\Lib\site-packages\pynput` and `.venv\Lib\site-packages\pystray`. That folder is their source
  code. Their projects are at https://github.com/moses-palmer/pynput and https://github.com/moses-palmer/pystray .
- To swap in a modified or different version that has the same interface, put your version in those folders or run
  `.venv\Scripts\python.exe -m pip install --force-reinstall <your package or folder>`; VoiceTool will use it the next
  time it starts. Nothing in VoiceTool stops you from modifying these libraries or from reverse engineering them to debug
  your changes.
- The full license texts: the GNU LGPL v3 is in `.venv\Lib\site-packages\pynput-1.8.2.dist-info\licenses\COPYING.LGPL`
  and `.venv\Lib\site-packages\pystray-0.19.5.dist-info\COPYING.LGPL`, and the GNU GPL v3 it builds on is in
  `.venv\Lib\site-packages\pystray-0.19.5.dist-info\COPYING`. They are also at https://www.gnu.org/licenses/lgpl-3.0.txt
  and https://www.gnu.org/licenses/gpl-3.0.txt .

### proxy_tools: a wheel VoiceTool built from PyPI's own file

**proxy_tools** 0.1.0 (MIT, Copyright Jonathan Tushman, https://github.com/jtushman/proxy_tools) is needed by pywebview. PyPI
only has its source file (`proxy_tools-0.1.0.tar.gz`, SHA-256
`ccb3751f529c047e2d8a58440d86b205303cf0fe8146f784d1cbcd94f0a28010`), and Setup installs ready-made wheels only, so VoiceTool
carries a wheel of it in the `wheels` folder: the one Python file from that source file, unchanged, packed into a wheel
(SHA-256 `b45619da198f7d013b287163a8d42958c7be0d5b5000c9f75aba6f5b7dbd202f`, listed in `requirements-friend.txt`). The code
in it comes from Werkzeug (BSD-3-Clause, Copyright 2013 Armin Ronacher; the BSD-3-Clause text is in section 7).

### certifi under MPL-2.0

**certifi** (a list of trusted internet certificates) is licensed under the Mozilla Public License 2.0. VoiceTool uses it
unmodified. Its source is at https://github.com/certifi/python-certifi , and the license text is in its `LICENSE` file under
`.venv\Lib\site-packages\certifi-2026.10.19.dist-info\licenses` and at https://www.mozilla.org/MPL/2.0/ .

### Parakeet: CC BY 4.0 attribution

Speech recognition uses **NVIDIA Parakeet TDT 0.6B v2**, created by NVIDIA and licensed under the Creative Commons
Attribution 4.0 International license (CC BY 4.0, https://creativecommons.org/licenses/by/4.0/). It was converted to ONNX
and quantized to int8 by Ilya Stupakov (https://huggingface.co/istupakov/parakeet-tdt-0.6b-v2-onnx). Those are changes to
the original model (https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2). The license does not grant any trademark or patent
rights, and it does not mean NVIDIA or Ilya Stupakov endorse VoiceTool. You are free to copy and use the model files
yourself under that license; nothing in VoiceTool restricts that.

### Not included

VoiceTool does not include or use PyMuPDF, ffmpeg, yt-dlp or any GPL or AGPL program. It does not include any model
other than the two listed in section 1.

## 7. License texts

MIT, the 3-clause BSD license and the Apache License 2.0 are printed below. For each package, the copyright holder is the
one named in the tables above. The other licenses (PSF, MIT-CMU, MPL-2.0, LGPL-3.0, GPL-3.0 and CC BY 4.0) are in the
packages' own license files in `.venv\Lib\site-packages` and at the links given above.

### MIT License (used by llama.cpp and most of the packages marked MIT)

```
MIT License

Copyright (c) <year> <the copyright holder named in the tables above>
(llama.cpp: Copyright (c) 2023-2026 The ggml authors)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### BSD 3-Clause License (used by the packages marked BSD-3-Clause)

```
BSD 3-Clause License

Copyright (c) <year>, <the copyright holder named in the tables above>
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### Apache License 2.0 (used by Qwen, openai, requests, flatbuffers, cryptography, packaging and the LLVM OpenMP runtime)

```
                                 Apache License
                           Version 2.0, January 2004
                        http://www.apache.org/licenses/

   TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION

   1. Definitions.

      "License" shall mean the terms and conditions for use, reproduction,
      and distribution as defined by Sections 1 through 9 of this document.

      "Licensor" shall mean the copyright owner or entity authorized by
      the copyright owner that is granting the License.

      "Legal Entity" shall mean the union of the acting entity and all
      other entities that control, are controlled by, or are under common
      control with that entity. For the purposes of this definition,
      "control" means (i) the power, direct or indirect, to cause the
      direction or management of such entity, whether by contract or
      otherwise, or (ii) ownership of fifty percent (50%) or more of the
      outstanding shares, or (iii) beneficial ownership of such entity.

      "You" (or "Your") shall mean an individual or Legal Entity
      exercising permissions granted by this License.

      "Source" form shall mean the preferred form for making modifications,
      including but not limited to software source code, documentation
      source, and configuration files.

      "Object" form shall mean any form resulting from mechanical
      transformation or translation of a Source form, including but
      not limited to compiled object code, generated documentation,
      and conversions to other media types.

      "Work" shall mean the work of authorship, whether in Source or
      Object form, made available under the License, as indicated by a
      copyright notice that is included in or attached to the work
      (an example is provided in the Appendix below).

      "Derivative Works" shall mean any work, whether in Source or Object
      form, that is based on (or derived from) the Work and for which the
      editorial revisions, annotations, elaborations, or other modifications
      represent, as a whole, an original work of authorship. For the purposes
      of this License, Derivative Works shall not include works that remain
      separable from, or merely link (or bind by name) to the interfaces of,
      the Work and Derivative Works thereof.

      "Contribution" shall mean any work of authorship, including
      the original version of the Work and any modifications or additions
      to that Work or Derivative Works thereof, that is intentionally
      submitted to Licensor for inclusion in the Work by the copyright owner
      or by an individual or Legal Entity authorized to submit on behalf of
      the copyright owner. For the purposes of this definition, "submitted"
      means any form of electronic, verbal, or written communication sent
      to the Licensor or its representatives, including but not limited to
      communication on electronic mailing lists, source code control systems,
      and issue tracking systems that are managed by, or on behalf of, the
      Licensor for the purpose of discussing and improving the Work, but
      excluding communication that is conspicuously marked or otherwise
      designated in writing by the copyright owner as "Not a Contribution."

      "Contributor" shall mean Licensor and any individual or Legal Entity
      on behalf of whom a Contribution has been received by Licensor and
      subsequently incorporated within the Work.

   2. Grant of Copyright License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      copyright license to reproduce, prepare Derivative Works of,
      publicly display, publicly perform, sublicense, and distribute the
      Work and such Derivative Works in Source or Object form.

   3. Grant of Patent License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      (except as stated in this section) patent license to make, have made,
      use, offer to sell, sell, import, and otherwise transfer the Work,
      where such license applies only to those patent claims licensable
      by such Contributor that are necessarily infringed by their
      Contribution(s) alone or by combination of their Contribution(s)
      with the Work to which such Contribution(s) was submitted. If You
      institute patent litigation against any entity (including a
      cross-claim or counterclaim in a lawsuit) alleging that the Work
      or a Contribution incorporated within the Work constitutes direct
      or contributory patent infringement, then any patent licenses
      granted to You under this License for that Work shall terminate
      as of the date such litigation is filed.

   4. Redistribution. You may reproduce and distribute copies of the
      Work or Derivative Works thereof in any medium, with or without
      modifications, and in Source or Object form, provided that You
      meet the following conditions:

      (a) You must give any other recipients of the Work or
          Derivative Works a copy of this License; and

      (b) You must cause any modified files to carry prominent notices
          stating that You changed the files; and

      (c) You must retain, in the Source form of any Derivative Works
          that You distribute, all copyright, patent, trademark, and
          attribution notices from the Source form of the Work,
          excluding those notices that do not pertain to any part of
          the Derivative Works; and

      (d) If the Work includes a "NOTICE" text file as part of its
          distribution, then any Derivative Works that You distribute must
          include a readable copy of the attribution notices contained
          within such NOTICE file, excluding those notices that do not
          pertain to any part of the Derivative Works, in at least one
          of the following places: within a NOTICE text file distributed
          as part of the Derivative Works; within the Source form or
          documentation, if provided along with the Derivative Works; or,
          within a display generated by the Derivative Works, if and
          wherever such third-party notices normally appear. The contents
          of the NOTICE file are for informational purposes only and
          do not modify the License. You may add Your own attribution
          notices within Derivative Works that You distribute, alongside
          or as an addendum to the NOTICE text from the Work, provided
          that such additional attribution notices cannot be construed
          as modifying the License.

      You may add Your own copyright statement to Your modifications and
      may provide additional or different license terms and conditions
      for use, reproduction, or distribution of Your modifications, or
      for any such Derivative Works as a whole, provided Your use,
      reproduction, and distribution of the Work otherwise complies with
      the conditions stated in this License.

   5. Submission of Contributions. Unless You explicitly state otherwise,
      any Contribution intentionally submitted for inclusion in the Work
      by You to the Licensor shall be under the terms and conditions of
      this License, without any additional terms or conditions.
      Notwithstanding the above, nothing herein shall supersede or modify
      the terms of any separate license agreement you may have executed
      with Licensor regarding such Contributions.

   6. Trademarks. This License does not grant permission to use the trade
      names, trademarks, service marks, or product names of the Licensor,
      except as required for reasonable and customary use in describing the
      origin of the Work and reproducing the content of the NOTICE file.

   7. Disclaimer of Warranty. Unless required by applicable law or
      agreed to in writing, Licensor provides the Work (and each
      Contributor provides its Contributions) on an "AS IS" BASIS,
      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
      implied, including, without limitation, any warranties or conditions
      of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or FITNESS FOR A
      PARTICULAR PURPOSE. You are solely responsible for determining the
      appropriateness of using or redistributing the Work and assume any
      risks associated with Your exercise of permissions under this License.

   8. Limitation of Liability. In no event and under no legal theory,
      whether in tort (including negligence), contract, or otherwise,
      unless required by applicable law (such as deliberate and grossly
      negligent acts) or agreed to in writing, shall any Contributor be
      liable to You for damages, including any direct, indirect, special,
      incidental, or consequential damages of any character arising as a
      result of this License or out of the use or inability to use the
      Work (including but not limited to damages for loss of goodwill,
      work stoppage, computer failure or malfunction, or any and all
      other commercial damages or losses), even if such Contributor
      has been advised of the possibility of such damages.

   9. Accepting Warranty or Additional Liability. While redistributing
      the Work or Derivative Works thereof, You may choose to offer,
      and charge a fee for, acceptance of support, warranty, indemnity,
      or other liability obligations and/or rights consistent with this
      License. However, in accepting such obligations, You may act only
      on Your own behalf and on Your sole responsibility, not on behalf
      of any other Contributor, and only if You agree to indemnify,
      defend, and hold each Contributor harmless for any liability
      incurred by, or claims asserted against, such Contributor by reason
      of your accepting any such warranty or additional liability.
```

The `requests` project's NOTICE file reads: "Requests. Copyright 2019 Kenneth Reitz".
