<p align="center">
  <img src="assets/sweepv.png" alt="SweepV" width="128" height="128">
</p>

<h1 align="center">SweepV</h1>

<p align="center">
  <b>Free up space on your Windows PC — safely.</b><br>
  See what's filling your disk, clean the junk Windows and your apps leave behind,<br>
  and ask AI before you delete anything you don't recognize.
</p>

<p align="center">
  <a href="https://github.com/denizvarici/SweepV/releases/latest/download/SweepV-win-x64.exe"><b>⬇ Download for Windows (64-bit)</b></a>
  &nbsp;·&nbsp;
  <a href="https://github.com/denizvarici/SweepV/releases/latest/download/SweepV-win-x86.exe">32-bit</a>
  &nbsp;·&nbsp;
  <a href="https://github.com/denizvarici/SweepV/releases">All releases</a>
</p>

---

## Download & run

1. Download **SweepV-win-x64.exe** (almost every PC today is 64-bit).
   Not sure? Open **Settings → System → About** and look at *System type*.
   If it says *32-bit*, download **SweepV-win-x86.exe** instead.
2. Double-click the file. There is nothing to install and nothing else to download.
3. Windows may show **"Windows protected your PC"** because the app is new and not yet signed.
   Click **More info → Run anyway**.

Works on Windows 10 and Windows 11.

> **If your antivirus complains:** cleanup tools do things that look unusual — deleting lots of
> files, touching system folders — so antivirus software sometimes flags them by behaviour, even
> though nothing malicious is happening. SweepV is open source: every line and every cleanup
> location is in this repository, and the downloads are built by GitHub from this code.
> If the single .exe is blocked, try the `.zip` download from the
> [releases page](https://github.com/denizvarici/SweepV/releases) instead.

> **Tip:** Some of the biggest savings (Windows Update leftovers, old Windows installations,
> system files) need administrator rights. SweepV shows a **Restart as administrator** button
> when that's the case.

## What it does

### 🧹 Quick Clean
Opens with a list of places that are safe to clean and shows how much space each one takes —
and the total you can get back — right at the top.

- **Windows leftovers:** temporary files, update caches, crash dumps, old logs, the Recycle Bin
- **Upgrade leftovers:** `Windows.old`, upgrade temp folders, graphics driver installers
- **Browsers and apps:** Chrome, Edge, Firefox, Discord, Teams, Telegram, Spotify, Steam and more
- **Developer caches:** npm, NuGet, pip, Gradle, JetBrains, Unity and others
- **System actions:** clean up the Windows component store, delete old restore points,
  shrink WSL / Docker virtual disks

Nothing is selected for you. Items marked **recommended** are safe for almost everyone, and
anything that could remove something you care about is marked with ⚠ and explained.
Locations that don't exist on your PC are simply not shown.

### ⚙️ Space-saving settings
Some Windows features quietly reserve many gigabytes — like the hibernation file or
Reserved Storage. SweepV tells you what each one costs and what you'd lose, and lets you
turn them **off or back on** at any time.

### 🔍 Disk Explorer
Scan a whole drive and browse folders sorted by size, like WizTree — so you can finally
see where your space went. Run as administrator for the fastest scans.

### 🤖 Ask AI about any folder
Found a huge folder and have no idea what it is? Select it and press **Ask AI**.
You'll get a plain-language answer: what it is, which app it belongs to, whether it's
safe to delete, and the right way to get rid of it. You can keep asking follow-up questions.

- Currently works with **Google Gemini**. Get a free API key at
  [aistudio.google.com](https://aistudio.google.com) and paste it into the AI panel.
  Support for more AI providers is planned.
- Only folder **names, paths and sizes** are sent — never the contents of your files.
- Your key is stored encrypted on your PC and only shown masked.

## Is it safe?

SweepV is built to be hard to misuse:

- It only cleans locations known to be safe, and tells you exactly what each one is.
- Files that are in use are skipped instead of forced.
- Anything that can't be undone asks for confirmation first.
- Disk Explorer is for looking and asking — it doesn't delete anything.

Still, it's your data: read the descriptions, and if you're unsure about something, leave it unchecked.

## Help improve SweepV

Know a folder that wastes space and is safe to clean? You can add it without writing any code —
the whole Quick Clean list is one simple file.
See **[Contributing cleanup locations](docs/CONTRIBUTING-CLEANUP-TARGETS.md)**.

Found a bug or have an idea? [Open an issue](https://github.com/denizvarici/SweepV/issues).

## License

[MIT](LICENSE) — free to use, share and modify.
