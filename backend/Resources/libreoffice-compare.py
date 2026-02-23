#!/usr/bin/env python3
# pyright: reportMissingImports=false
import os
import re
import sys
import time
import traceback
import zipfile


def _fail(message: str, code: int = 1) -> int:
    sys.stderr.write(message.strip() + "\n")
    return code


def _prop(name, value):
    from com.sun.star.beans import PropertyValue

    prop = PropertyValue()
    prop.Name = name
    prop.Value = value
    return prop


def _to_file_url(path: str) -> str:
    import uno

    return uno.systemPathToFileUrl(os.path.abspath(path))


def _connect(host: str, port: int):
    import uno

    local_ctx = uno.getComponentContext()
    resolver = local_ctx.ServiceManager.createInstanceWithContext(
        "com.sun.star.bridge.UnoUrlResolver", local_ctx
    )
    return resolver.resolve(
        f"uno:socket,host={host},port={port};urp;StarOffice.ComponentContext"
    )


def _has_word_track_changes(docx_path: str) -> bool:
    try:
        with zipfile.ZipFile(docx_path, "r") as zf:
            for name in zf.namelist():
                if not name.startswith("word/") or not name.endswith(".xml"):
                    continue
                xml = zf.read(name).decode("utf-8", "ignore")
                if re.search(r"<w:(ins|del)(?:\s|>)", xml):
                    return True
    except Exception:
        return False

    return False


def _load_document_with_retry(desktop, url: str, attempts: int = 20):
    load_arg_variants = [
        (_prop("Hidden", True), _prop("ReadOnly", False)),
        (_prop("Hidden", True),),
        (_prop("Hidden", True), _prop("ReadOnly", True)),
    ]
    last_error = ""
    for attempt in range(attempts):
        for load_args in load_arg_variants:
            try:
                document = desktop.loadComponentFromURL(url, "_blank", 0, load_args)
                if document is not None:
                    return document, ""
                last_error = "loadComponentFromURL returned None"
            except Exception as exc:
                last_error = str(exc)

        if attempt < attempts - 1:
            time.sleep(0.5)

    return None, last_error


def run_probe(host: str, port: int) -> int:
    try:
        _connect(host, port)
        return 0
    except Exception as exc:
        return _fail(f"UNO probe failed: {exc}")


def run_compare(host: str, port: int, original_path: str, corrected_path: str, output_path: str, author: str) -> int:
    del author
    document = None
    try:
        ctx = _connect(host, port)
        smgr = ctx.ServiceManager
        desktop = smgr.createInstanceWithContext("com.sun.star.frame.Desktop", ctx)

        original_url = _to_file_url(original_path)
        corrected_url = _to_file_url(corrected_path)
        output_url = _to_file_url(output_path)

        document, corrected_load_error = _load_document_with_retry(desktop, corrected_url)
        if document is None:
            _, original_load_error = _load_document_with_retry(desktop, original_url)
            corrected_exists = os.path.exists(corrected_path)
            original_exists = os.path.exists(original_path)
            corrected_size = os.path.getsize(corrected_path) if corrected_exists else -1
            original_size = os.path.getsize(original_path) if original_exists else -1
            return _fail(
                "LibreOffice UNO compare failed: could not load corrected document for compare base. "
                f"corrected load error: {corrected_load_error or 'unknown'}; "
                f"original load error: {original_load_error or 'unknown'}; "
                f"corrected exists={corrected_exists} size={corrected_size}; "
                f"original exists={original_exists} size={original_size}; "
                f"corrected_path={os.path.abspath(corrected_path)}; "
                f"original_path={os.path.abspath(original_path)}"
            )

        frame = document.getCurrentController().getFrame()
        dispatcher = smgr.createInstanceWithContext("com.sun.star.frame.DispatchHelper", ctx)

        try:
            document.RecordChanges = True
            document.ShowChanges = True
        except Exception:
            pass

        dispatcher.executeDispatch(
            frame,
            ".uno:CompareDocuments",
            "",
            0,
            (_prop("URL", original_url),),
        )

        total_bytes = 0
        try:
            total_bytes = os.path.getsize(original_path) + os.path.getsize(corrected_path)
        except Exception:
            pass

        wait_seconds = min(20.0, max(8.0, (total_bytes / (1024.0 * 1024.0)) * 1.5 + 6.0))
        time.sleep(wait_seconds)

        document.storeToURL(
            output_url,
            (
                _prop("FilterName", "MS Word 2007 XML"),
                _prop("Overwrite", True),
            ),
        )

        try:
            document.close(True)
        except Exception:
            pass
        document = None

        return 0
    except Exception:
        return _fail("LibreOffice UNO compare script failed:\n" + traceback.format_exc())
    finally:
        if document is not None:
            try:
                document.close(True)
            except Exception:
                pass


def main() -> int:
    if len(sys.argv) < 2:
        return _fail(
            "Usage:\n"
            "  libreoffice-compare.py probe <host> <port>\n"
            "  libreoffice-compare.py compare <host> <port> <originalPath> <correctedPath> <outputPath> <author>"
        )

    mode = sys.argv[1].strip().lower()
    if mode == "probe":
        if len(sys.argv) != 4:
            return _fail("Probe mode requires: <host> <port>")
        return run_probe(sys.argv[2], int(sys.argv[3]))

    if mode == "compare":
        if len(sys.argv) != 8:
            return _fail("Compare mode requires: <host> <port> <originalPath> <correctedPath> <outputPath> <author>")
        return run_compare(
            sys.argv[2],
            int(sys.argv[3]),
            sys.argv[4],
            sys.argv[5],
            sys.argv[6],
            sys.argv[7],
        )

    return _fail(f"Unknown mode: {mode}")


if __name__ == "__main__":
    sys.exit(main())
