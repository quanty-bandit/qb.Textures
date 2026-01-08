# qb.Textures
Textures loading and cache management

## CONTENT

**WebTextureCacheHandler**

Handles caching, loading, and management of web textures, supporting multiple formats and device cache with etag validation.

**USTextureAtlas**

Manages a texture atlas for storing multiple frames or textures with a same resolution, providing utilities for frame addition, UV generation, and sprite creation.

**GifParser**

Gif parser utility class

## HOW TO INSTALL

Use the Unity package manager and the Install package from git url option.

- Install at first time,if you haven't already done so previously, the package <mark>[unity-package-manager-utilities](https://github.com/sandolkakos/unity-package-manager-utilities.git)</mark> from the following url: 
  [GitHub - sandolkakos/unity-package-manager-utilities: That package contains a utility that makes it possible to resolve Git Dependencies inside custom packages installed in your Unity project via UPM - Unity Package Manager.](https://github.com/sandolkakos/unity-package-manager-utilities.git)

- Next, install the package from the current package git URL. 
  
  All other dependencies of the package should be installed automatically.

## Dependencies

- https://github.com/quanty-bandit/qb.Pattern.git
- https://github.com/quanty-bandit/qb.Threading.git

## Thirdpart embeded source code 
https://github.com/3DI70R/Unity-GifDecoder.git for gif image decoding.