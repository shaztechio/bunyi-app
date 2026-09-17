<!--
Copyright 2026 Shazron Abdullah and Bunyi contributors

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
-->

# Using Bunyi with an NVIDIA GPU

The optional **CUDA build** can run the speech model on an NVIDIA GPU. Bunyi
detects acceleration automatically; there is no switch to enable in Settings.
If it cannot use CUDA, it falls back to the CPU. The final audio-decoding step
always runs on the CPU, so some CPU activity is normal.

This guide covers the Windows/Linux **1.3.1 builds**, which bundle ONNX Runtime
**1.29.0**. Dependency versions were checked on **15 September 2026**. Recheck
the [ONNX Runtime compatibility table](https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html#requirements)
when Bunyi updates its runtime. macOS uses MLX and does not use this guide.

## What to install

| Component | Version / purpose | Included with Bunyi? |
|---|---|---|
| NVIDIA GPU and display driver | Hardware supported by CUDA 13 and a compatible driver; use NVIDIA's current driver for your GPU and OS | No |
| CUDA Toolkit | **13.x**, including its runtime, cuBLAS and cuRAND libraries; Bunyi has been measured with **13.3** | No |
| cuDNN | **9.x built for CUDA 13**, recommended to match ONNX Runtime's supported dependency set; see the note below | No |
| Bunyi CUDA edition | Filename contains **`-cuda`** | Download separately from the standard edition |
| .NET and ONNX Runtime | Bundled in the Bunyi download | Yes |
| Microsoft Visual C++ runtime | Bundled with the Windows setup EXE | Yes, in the installer |

**CUDA 12 alone does not satisfy this build.** Its libraries have different
names from the CUDA 13 libraries the application loads. ONNX Runtime lists
1.29.x against CUDA 13.0 and cuDNN 9.x, with compatibility within CUDA 13.x.
[NVIDIA's driver compatibility table](https://docs.nvidia.com/deploy/cuda-compatibility/minor-version-compatibility.html)
lists driver family 580 or newer for CUDA 13; use the driver recommended for
your selected toolkit and GPU rather than treating that family minimum as a
guarantee for every feature or card.

**About cuDNN:** the recommended complete setup includes it, but Bunyi's
current Windows speech pipeline has also been measured working without it:
the operation that needed cuDNN was in the audio decoder, which Bunyi runs on
CPU. That Windows measurement is not a guarantee for every model or Linux
configuration. The [research record](apps/dotnet/RESEARCH-ONNX.md#cuda-on-our-own-pipeline-and-what-it-costs-to-reach)
explains the distinction. If an existing setup already reports CUDA and
generates successfully, there is no need to change it just for this guide.

You do not need Python, PyTorch or the .NET SDK to run Bunyi. NVIDIA's toolkit
contains development tools, but Bunyi uses its runtime libraries.

## Windows: install and launch

1. Install the appropriate [NVIDIA display driver](https://www.nvidia.com/drivers/)
   for your GPU and Windows version. Restart if requested. CUDA Toolkit 13
   does not install the Windows display driver for you.
2. Download a **CUDA 13.x Toolkit** from the
   [NVIDIA toolkit archive](https://developer.nvidia.com/cuda-toolkit-archive).
   Select Windows and x86_64, then run the installer. **13.3** is the version
   used in Bunyi's recorded Windows tests. Keep the CUDA runtime and math
   libraries selected; Visual Studio integration is not needed to run Bunyi.
   [NVIDIA's Windows installation guide](https://docs.nvidia.com/cuda/cuda-installation-guide-microsoft-windows/)
   covers the installer options.
3. For the complete dependency set, install **cuDNN 9 for CUDA 13** with
   [NVIDIA's cuDNN graphical installer](https://docs.nvidia.com/deeplearning/cudnn/installation/latest/windows.html).
   Select the CUDA 13 variant. The CUDA Toolkit does not include cuDNN.
4. Check that Windows can find the libraries. In Start, search for
   **Edit environment variables for your account**. Edit your **Path**, choose
   **New**, and add the actual library directories if they are missing:
   - For a default CUDA 13.3 installation:
     `C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.3\bin\x64`.
     Use the folder containing `cublasLt64_13.dll`, `cublas64_13.dll` and
     `cudart64_13.dll`; CUDA 13 uses `bin\x64` rather than the old `bin` layout.
   - If you installed cuDNN, add the folder containing `cudnn64_9.dll` and
     its companion DLLs. Its exact directory depends on the installer version;
     inspect the installed cuDNN folder instead of pasting a guessed version.
   Add entries to Path; keep its existing entries. Sign out and back in so
   applications started from Start inherit the updated environment.
5. From the [Windows/Linux releases](https://github.com/shaztechio/bunyi-app/releases),
   download **`Bunyi-<version>-win-x64-cuda-setup.exe`** and its `.sha256` file.
   For 1.3.1 the filename is `Bunyi-1.3.1-win-x64-cuda-setup.exe`.
   Close Bunyi, run setup, then open **Bunyi** from Start. Check the release notes
   for signing status: signed installers identify **Shazron Elhazar Abdullah**
   as the publisher; the older 1.3.1 files remain unsigned. See the
   [signing policy](README.md#code-signing-policy).
6. Follow [Verify acceleration](#verify-acceleration) below.

The CUDA installer replaces the standard edition in the same location and
preserves models, voices, recordings and settings. To switch back, download
and run the standard **`Bunyi-<version>-win-x64-setup.exe`**. No uninstall is
needed. Neither installer downloads NVIDIA drivers, CUDA or cuDNN.

### Optional Windows diagnostics

Open a **new PowerShell window** after changing Path:

```powershell
nvidia-smi
where.exe cublasLt64_13.dll
where.exe cublas64_13.dll
where.exe cudart64_13.dll
# If cuDNN was installed:
where.exe cudnn64_9.dll
```

`nvidia-smi` checks the GPU/driver. Its displayed **CUDA Version** is the
driver's supported version, not proof that the toolkit libraries are installed.
The `where.exe` commands should return your selected CUDA/cuDNN directories.
If several copies appear, check Path ordering for obsolete installations.
Installing a Python CUDA/cuDNN package alone does not make its libraries
available to Bunyi launched from Start.

## Linux: install dependencies and use the CUDA archive

1. Install an NVIDIA driver using the instructions for your distribution in
   [NVIDIA's driver guide](https://docs.nvidia.com/datacenter/tesla/driver-installation-guide/).
   Restart if requested and check that `nvidia-smi` detects the GPU.
2. Use the [CUDA download selector](https://developer.nvidia.com/cuda-downloads)
   or [toolkit archive](https://developer.nvidia.com/cuda-toolkit-archive) to
   choose **CUDA 13.x**, Linux, x86_64, and your distribution/version. Follow
   NVIDIA's **package-manager** installation steps for that exact combination.
   [The Linux guide](https://docs.nvidia.com/cuda/cuda-installation-guide-linux/)
   covers Ubuntu, Debian, Fedora and other supported systems. Use the complete
   toolkit/runtime-library set, including cuBLAS and cuRAND.
3. For the complete dependency set, follow the
   [cuDNN 9 installation guide](https://docs.nvidia.com/deeplearning/cudnn/installation/latest/linux.html)
   for **CUDA 13** and a supported distribution. For Ubuntu/Debian, *after
   enabling the matching NVIDIA repository*, its package command is:

   ```sh
   sudo apt-get update
   sudo apt-get install cudnn9-cuda-13 zlib1g
   ```

   Other distributions need the appropriate documented package or archive
   method. CUDA and cuDNN can support different distribution versions; do not
   substitute a repository for another distribution.
4. Download **`Bunyi-<version>-linux-x64-cuda.tar.gz`** and its `.sha256` file
   from the same release. The `.deb` and `.rpm` installers are **CPU editions**;
   Linux CUDA is currently a portable archive. Extract the whole archive,
   open the extracted directory, and launch `./Bunyi.App`.

   Bunyi also needs its usual desktop/audio dependencies. On a supported
   Ubuntu/Debian/Fedora desktop, installing Bunyi's standard `.deb`/`.rpm`
   first is one way to install those automatically. Then close it and launch
   the CUDA executable from the extracted directory; the standard package's
   application-menu entry still opens the CPU edition. Both use the same data.

NVIDIA's system packages normally configure library discovery. With a manual
toolkit/archive installation, follow its post-installation steps. For a CUDA
13.3 installation under `/usr/local/cuda-13.3`, a terminal-only launch example
from Bunyi's extracted directory is:

```sh
LD_LIBRARY_PATH="/usr/local/cuda-13.3/lib64${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" ./Bunyi.App
```

Use your actual directory and include cuDNN's library directory too if it was
installed outside the system search path. This setting applies only to that
launch; it does not configure application-menu launches. Do not use CUDA's
`stubs` directory as a runtime library path: the real `libcuda.so.1` comes
from the NVIDIA driver.

For a missing-library error, run this from the trusted, extracted Bunyi release:

```sh
ldd ./libonnxruntime_providers_cuda.so
```

Resolve any **not found** entries. The shipped Linux provider references
`libcublasLt.so.13`, `libcublas.so.13`, `libcudart.so.13`, `libcurand.so.10`
and the driver's `libcuda.so.1`. **`libcurand.so.10` is expected even with
CUDA 13**; its library version does not mean you should install CUDA 10.

## Verify acceleration

1. Close and reopen Bunyi after installing libraries or changing the environment.
2. Open **Doctor** and find **Acceleration**. It should report that the speech
   model runs on **CUDA**.
3. Generate a short sentence. In **Logs**, look for:

   ```text
   Speech model on CUDA, audio step on CPU.
   ```

4. Check for a later message saying CUDA could not run a model and the run
   moved to CPU. Successful detection is not proof that every model fits on
   the GPU. CPU audio decoding is expected and does not indicate fallback.

If CUDA fails during model loading, Bunyi remembers CPU fallback until you
restart it. The optional **`BUNYI_EP=cpu`** debugging environment variable also
forces CPU; remove that override if you previously set it. Normal use needs
no environment-variable override to select CUDA.

## Common problems

| Symptom | What to check |
|---|---|
| Doctor reports CPU | Confirm the download contains `-cuda`, then check the driver and CUDA 13 library paths. Restart Bunyi after changes. |
| Missing `cublasLt64_13.dll` / `libcublasLt.so.13` | Install CUDA 13's cuBLAS libraries; CUDA 12 alone cannot supply them. |
| Missing `cudnn64_9.dll` / `libcudnn.so.9` | Install cuDNN 9 for CUDA 13 and make its runtime directory visible. |
| Works in a terminal, opens on CPU from Start/menu | The desktop process has a different environment, or its shortcut opens the standard edition. Check paths and sign out/in after Windows Path changes. |
| CUDA starts but a model fails or runs out of GPU memory | Close other GPU-heavy apps and retry a short sentence after restarting Bunyi. Read the fallback message; use the standard CPU build if needed. |
| AMD/Intel GPU, or an NVIDIA GPU unsupported by CUDA 13 | Use the standard CPU build. Installing the CUDA edition does not add support for those GPUs. |

The standalone **`Bunyi-CLI-…-cuda`** archives use the same dependencies;
the desktop installer does not install the CLI. See the
[CLI guide](apps/dotnet/CLI.md) for running it.

For a support request, include your Bunyi version, OS, GPU, driver/toolkit
versions, Doctor's Acceleration result and the relevant log error. Packaging
tests without an NVIDIA GPU do not establish hardware acceleration support.
