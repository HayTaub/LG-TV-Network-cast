# Third-party notices

## FFmpeg

LG Network Cast ships an unmodified `ffmpeg.exe` from the
[gyan.dev "essentials" build](https://www.gyan.dev/ffmpeg/builds/) and runs it as a separate process
for screen capture and H.264 encoding.

FFmpeg is licensed under the GNU General Public License v3 (this build includes GPL components such as
libx264). LG Network Cast does not link to FFmpeg; it only starts the executable.

- FFmpeg source code: https://ffmpeg.org/download.html
- Build information and sources for this build: https://www.gyan.dev/ffmpeg/builds/
- License text: https://www.gnu.org/licenses/gpl-3.0.html

FFmpeg is a trademark of Fabrice Bellard, originator of the FFmpeg project.
