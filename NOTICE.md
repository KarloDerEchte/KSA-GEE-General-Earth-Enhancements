# License and third-party material

## GEE - General Earth Enhancements

Copyright (C) 2026 KarloDerEchte and the GEE contributors

GEE's source code and its procedurally generated meshes and textures are free software: you can redistribute
and/or modify them under the terms of the GNU General Public License version 3 (`LICENSE`). Anyone who distributes a
modified version must make its complete source code available under the same license.

This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied
warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.

**Additional permission under GNU GPL version 3 section 7.** If you modify this Program, or any covered work, by
linking or combining it with Kitten Space Agency, the StarMap mod loader or Harmony (or modified versions of those),
containing parts covered by the terms of their own licenses, the licensors of this Program grant you additional
permission to convey the resulting work. Corresponding Source for a non-source form of such a combination need not
include the source code of the parts of Kitten Space Agency, StarMap or Harmony used, but must include the source
code of the covered work.

## Third-party data, models and services

GEE uses the following third-party material, under these terms.

### Downloaded while you play (not part of this repository)

| What | From | License / terms | Attribution |
|---|---|---|---|
| Buildings, roads, places (vector tiles) | [OpenFreeMap](https://openfreemap.org) (`tiles.openfreemap.org`), OpenMapTiles schema | Map data [ODbL 1.0](https://opendatacommons.org/licenses/odbl/1-0/); free public tile service, no API key | © OpenMapTiles © OpenStreetMap contributors |
| Satellite imagery (seen from altitude) | [EOxCloudless](https://cloudless.eox.at), WMTS layer `s2cloudless_3857` (2016 edition) | [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) for the 2016 edition (later editions are non-commercial only: do not switch years without checking) | EOxCloudless https://cloudless.eox.at by EOX IT Services GmbH (Contains modified Copernicus Sentinel data 2016) |
| Land cover for tree placement | [ESA WorldCover](https://esa-worldcover.org) 2021 v200, Cloud Optimized GeoTIFFs from the AWS Open Data bucket `esa-worldcover` | [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) | © ESA WorldCover project 2021 / Contains modified Copernicus Sentinel data (2021) processed by ESA WorldCover consortium |
| Street lamps and lit roads (optional, off by default) | An [Overpass API](https://wiki.openstreetmap.org/wiki/Overpass_API) server set in `StreetLightOverpassUrls` | Map data ODbL 1.0; the public servers follow a [usage policy](https://dev.overpass-api.de/overpass-doc/en/preface/commons.html) that asks apps used by many people to run their own instance | © OpenStreetMap contributors |

Downloads are cached under `cache/` in the mod folder. The cache is your local copy and is not distributed with the
mod.

### Included in this repository

| What | Where | License | Attribution |
|---|---|---|---|
| Pipelines at LC-39A; tanks and the payload tent at LC-39A, Starbase, SLC-4E and LC-1; positions and outlines of the modelled towers and buildings | `src/PipelineData.cs`, `src/StructureData.cs`, `src/Landmarks.cs` | [ODbL 1.0](https://opendatacommons.org/licenses/odbl/1-0/): these extracts are derived from OpenStreetMap and stay under the ODbL | © OpenStreetMap contributors |
| LC-39 fixed service structure (launch tower) mesh | `src/tools/data/fss_nasa.npy`, built into `Meshes/` | NASA 3D Resources model ["Gantry"](https://science.nasa.gov/3d-resources/gantry/) (NASA media, generally not subject to copyright). Its use does not imply endorsement by NASA. | NASA / Michael D. Carbajal |
| Road surfaces (asphalt of roads, streets, ramps, highways and junction pads) | Built into `Textures/` by `src/tools/make_assets.py` from the scan, which it downloads to `src/tools/data/polyhaven/` (not part of this repository) | [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) | [Poly Haven](https://polyhaven.com), "Asphalt Pit Lane" by Dimitrios Savva (https://polyhaven.com/a/asphalt_pit_lane) |

All other meshes and textures (facades, roofs, the VAB and hangar facades, towers, tanks, crew access arm, road
markings) are generated procedurally by the scripts in `src/tools/` and are covered by the GPL like the code. They contain no agency
insignia or company logos. Photos used as visual reference while modelling are not included.

### Not included

The mod builds against Kitten Space Agency's own assemblies, StarMap and Harmony, but ships none of them. Install the
game and StarMap yourself.
