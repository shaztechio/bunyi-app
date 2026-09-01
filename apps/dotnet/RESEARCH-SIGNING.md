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

<!--
  The document below is kept as it was written. The status block is the part
  that goes stale, so it is dated and sits above it rather than woven in.
-->

## Status, as of 2026-09-01

- **SignPath Foundation — applied for and rejected.** That is what prompted
  this document. Every claim that Windows builds are signed was removed from
  the README, the site and the .NET release notes when the rejection came
  through (#170).
- **Certum — in progress**, tracked in
  [#171](https://github.com/shaztechio/bunyi-app/issues/171). An open-source
  code signing certificate is being obtained from
  [Certum](https://www.certum.eu/), and that is the live route. Note it is an
  **OV** certificate: SmartScreen reputation attaches to the certificate and
  starts at zero, so early signed builds can still warn.
  **The document below predates it and does not mention it**, so read its
  recommendation as the fallback written when no certificate was in prospect,
  not as the current plan.
- **Nothing ships signed today.** Windows builds are unsigned and say so in all
  three places a user might look: the README, the site, and the release notes.

Steps 2 and 3 below survive the Certum route either way. winget submission
builds reputation regardless of signing, and the SmartScreen disclaimer is what
the app needs until a certificate is actually in the pipeline — that wording is
now in `README.md` and on the site.

Once a certificate exists, the work is in
[`.github/workflows/dotnet-release.yml`](../../.github/workflows/dotnet-release.yml):
a signing step in the packaging job, and the code signing policy section of the
release notes, which was reduced to a plain statement when the SignPath branch
was removed.

---

# Windows Code Signing: Action Plan for Open-Source Developers

This action plan is tailored for independent developers releasing open-source C# (Avalonia) applications on Windows, providing practical workarounds to bypass or manage Microsoft SmartScreen without commercial costs.

---

## 📋 Recommended Strategy
Combine **Microsoft Store Distribution** (for a flawless, warning-free user experience) with an **Unsigned GitHub Release** backed by a clear user disclaimer for advanced users.

---

## 🛠️ Step-by-Step Execution

### Step 1: Package and Publish to the Microsoft Store
This is the single best way to get your app signed with an official Microsoft certificate for free, completely bypassing SmartScreen.

1. **Register as a Partner:** Create an individual developer account on the [Microsoft Partner Center](https://partner.microsoft.com/dashboard/registration) (requires a one-time fee of ~ $19 USD).
2. **Package your Avalonia App:** Package your application as an `.msix` bundle rather than a standard standalone `.exe`.
3. **Submit for Review:** Upload the package to the Partner Center dashboard. Once approved, Microsoft signs the binary on their backend. Users can download it directly through the Store or via Windows Terminal without any warnings.

### Step 2: Submit to Windows Package Manager (winget)
Even if you distribute an unsigned `.exe` on GitHub, submitting it to the official Windows package repository builds automated telemetry trust with Microsoft.

1. **Prepare the Manifest:** Use the `wingetcreate` CLI tool to generate a package manifest pointing to your GitHub release.
2. **Submit a PR:** Submit the manifest to the official [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) GitHub repository.
3. **Reputation Building:** As users install your app via `winget install <your-app-id>`, Microsoft's telemetry logs the file hash as safe, gradually lowering the SmartScreen barrier over time.

### Step 3: Add a SmartScreen Disclaimer to your GitHub README
For users downloading the standalone `.exe` or `.msi` directly from your GitHub Releases, add a transparent notice to manage expectations and provide clear safety instructions.

> #### ⚠️ Note for Windows Users (SmartScreen Warning)
> Because this is an independent, open-source project, this standalone executable is **not signed with a commercial EV certificate** (which costs hundreds of dollars annually). 
> 
> When running the installer for the first time, Windows SmartScreen may display a warning stating *"Windows protected your PC"*. 
> * **To install anyway:** Click **"More info"** and then select **"Run anyway"**.
> * **Alternative (Verified):** If you prefer a completely warning-free installation, you can download our officially signed version directly from the **Microsoft Store** or run `winget install <YourAppID>` in your terminal.

---

## 🔍 Alternative Pipelines (If SignPath Becomes Feasible)
If your repository gains more traction (stars, forks, or contributions) over time, re-apply to the **SignPath Open Source Foundation**. Once approved:
* Integrate their [SignPath GitHub Action](https://github.com/marketplace/actions/signpath-artifact-signing) into your CI/CD pipeline.
* This will automatically sign your production builds using their free, community-backed certificate slot.
