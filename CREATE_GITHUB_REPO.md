# One-time GitHub setup

This folder is already a local Git repository on the `main` branch.

## 1. Create an empty PUBLIC repository on GitHub

Recommended name: `BDOLootTracker`

Do not add a README/.gitignore/license on GitHub during creation because those files are already in this project.

## 2. Connect this local repository

From the project folder:

```powershell
git remote add origin https://github.com/YOUR_GITHUB_NAME/BDOLootTracker.git
git push -u origin main
```

## 3. Create the first installer/release

```powershell
git tag v0.9.2
git push origin v0.9.2
```

GitHub Actions will build and publish the installer automatically.

Open the repository's **Actions** tab to follow the build. When it completes, **Releases** will contain the Velopack installer and update assets.

## 4. Test auto-update

Install `v0.9.2` from the GitHub Release Setup executable.

Then make a small code change and create a newer release:

```powershell
git add .
git commit -m "Prepare v0.9.3"
git push

git tag v0.9.3
git push origin v0.9.3
```

On the next launch of the installed v0.9.2 tracker, it checks the public GitHub Releases and offers v0.9.3 automatically.
