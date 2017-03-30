## WARC mounter
Mounts WARC files (web archives).

Usage: `Shaman.Dokan.Warc.exe <path-to-cdx> [--open]`

The archive(s) are mounted in `C:\DokanFs\path-to-cdx`. When `--open` is specified, the mounted folder is opened in Explorer.

### Screenshot
![WARC mounter screenshot](https://raw.githubusercontent.com/antiufo/Shaman.Dokan.Warc/master/images/mount-warc.png)

### Features
* Read only file system
* `Last-Modified` as last write time
* Dynamically rewrites links when an HTML page is opened (use the `\raw` virtual directory to disable dyamic rewrite)
