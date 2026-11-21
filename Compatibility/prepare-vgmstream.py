"""Expose Ogg's target alias before configuring newer Vorbis sources."""
import pathlib
import sys

path = pathlib.Path(sys.argv[1]) / "cmake/dependencies/ogg.cmake"
source = path.read_text()
anchor = "\t\t\tset(OGG_LIBRARY ogg)"
includes = "\n\t\t\ttarget_include_directories(ogg INTERFACE \"$<BUILD_INTERFACE:${OGG_PATH}/include>\" \"$<BUILD_INTERFACE:${OGG_BIN}/include>\")"
replacement = anchor + includes + "\n\t\t\tif(NOT TARGET Ogg::ogg)\n\t\t\t\tadd_library(Ogg::ogg ALIAS ogg)\n\t\t\tendif()"
previous = anchor + "\n\t\t\tif(NOT TARGET Ogg::ogg)\n\t\t\t\tadd_library(Ogg::ogg ALIAS ogg)\n\t\t\tendif()"
previous_includes = anchor + "\n\t\t\tset_target_properties(ogg PROPERTIES INTERFACE_INCLUDE_DIRECTORIES \"${OGG_PATH}/include;${OGG_BIN}/include\")" + previous[len(anchor):]
source = source.replace(previous_includes, anchor)
source = source.replace(previous, anchor)
if replacement not in source:
    if source.count(anchor) != 1:
        raise RuntimeError("The pinned vgmstream Ogg target configuration changed.")
    path.write_text(source.replace(anchor, replacement))

mpeg = path.parent / "mpg123.cmake"
source = mpeg.read_text()
anchor = "\t\t\tif(EMSCRIPTEN)"
replacement = "\t\t\tif(CMAKE_OSX_ARCHITECTURES STREQUAL \"x86_64\")\n\t\t\t\tlist(APPEND MPEG_CONFIGURE --host=x86_64-apple-darwin --with-cpu=generic_fpu)\n\t\t\tendif()\n" + anchor
if replacement not in source:
    if source.count(anchor) != 1:
        raise RuntimeError("The pinned mpg123 configure arguments changed.")
    mpeg.write_text(source.replace(anchor, replacement))
