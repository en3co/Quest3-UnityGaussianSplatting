# Quest 3 Gaussian Splat Sample

A Unity template for a Quest 3 experience built around one Gaussian splat. Open it, build it to the headset, and use it as the starting point for your own scan. This project also includes tools to subsample and merge `.ply` files before they are packed for the renderer.

It is built on [Unity Gaussian Splatting](https://github.com/aras-p/UnityGaussianSplatting) by Aras Pranckevičius, so credit goes to that repo for a lot of the foundations. The package is included under `package/` and remains under its MIT license (`LICENSE.md`). The Quest scene, the `GaussianSplatRenderer` setup, locomotion, and the PLY tools in `GaussianExample/` are my additions on top of it.

Open the Unity project at `GaussianExample/`, not the folder above it. The package is referenced as `file:../../package`, so keep `GaussianExample/` and `package/` together.

## What you get

- A Unity `6000.3.10f1` project aimed at Quest 3
- Scene `Assets/Scenes/Example Splat.unity`
- A customised `GaussianSplatRenderer` on the sample object, set to Debug Points, with the Quest camera hooked up so the splat draws in stereo in the built app
- One example splat, Chapel Door, placed and scaled for the headset
- A black skybox and a floor you can walk on around the splat in the void
- Controller locomotion: standard Quest left stick to walk, right stick to turn
  - Hand tracking is enabled, but the movement stays on the controllers
- Editor tools for subsampling and merging binary PLY files

## Run it on Quest 3

The Game view is only a flat preview - the stereo rendering is obviously meant to be judged in the headset, which I personally test iteratively through a quick build for best quality in performance etc., activated either in the editor or nowadays, instigated by your agent of choice.

1. Open `GaussianExample` in Unity `6000.3.10f1`.
2. Open `Assets/Scenes/Example Splat`.
3. Build that scene to the connected Quest 3. The Android settings are already set for Quest 3: ARM64, minimum SDK 32, Horizon OS 60 through 209, and OpenXR.
4. Install the APK with `adb install -r` if the build does not install it for you.

The app should only ask for hand tracking - it does not need scene or spatial-anchor access.

The Chapel Door object is already placed and scaled to be proportional in the headset. Mileage may vary with other splats, so adjust your own.

## Add your own scan

1. Start from a binary little-endian `.ply`. In the PLY tools, a `~` at the start of a path is expanded to your home folder. The path needs a file name. Do not wrap it in quotes.
2. If the file is larger than you want on Quest, or you want two scans in one file, use the tools in the next section. Skip this step if the PLY is already the file you want to ship.
3. Pack that PLY with `Tools > Gaussian Splats > Create GaussianSplatAsset`. Subsample and Merge only write a `.ply`. They do not create the Unity asset for the scene.
4. In `Example Splat`, select the splat object and assign the new asset on its `GaussianSplatRenderer`. The example uses Debug Points. Splats mode is the fuller gaussian render, if you want that instead.
5. Place and scale the object in the Editor, then judge it in the headset.
6. Build to the Quest.

## Included PLY tools

Both tools are under `Tools > Gaussian Splats`. They read and write binary little-endian PLY, the same kind of file the asset creator accepts.

**Subsample PLY**
Keeps a random subset of the points and writes a new file. Dragging Keep updates the output filename to the number of points it will write, while that filename is still the automatic one.

**Merge PLY**
Joins two scans into one file.

With **Use scene placement** on, assign the two splat objects only. Move, rotate, and scale them in the Scene, then merge. The tool reads the splat asset on each object and writes one `.ply` in those world positions. The positions are already in the file. Assign that file to an object left at position 0, rotation 0, and scale 1. If that object is also moved or scaled, the placement is applied a second time.

Turn **Use scene placement** off to merge two `.ply` filepaths instead. Those fields are hidden while scene placement is on. In this mode the points stay in the coordinates stored in each file, and both files must list the same properties in the same order: position, normals, `f_dc`, `f_rest`, opacity, scale, and rotation. View-dependent colour is copied as stored. Debug Points, and Splats with SH order 0, do not use that colour.

**Subsample after merge** on the same window can thin the result before the file is written.

After either tool, go back to **Add your own scan** and input the PLY with `Create GaussianSplatAsset`.
