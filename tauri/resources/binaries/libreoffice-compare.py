#!/usr/bin/env python3
# pyright: reportMissingImports=false
import os
import sys
import traceback


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

        load_args = (
            _prop("Hidden", True),
            _prop("ReadOnly", False),
        )
        document = desktop.loadComponentFromURL(corrected_url, "_blank", 0, load_args)
        if document is None:
            return _fail("LibreOffice UNO compare failed: could not load corrected document.")

        frame = document.getCurrentController().getFrame()
        dispatcher = smgr.createInstanceWithContext("com.sun.star.frame.DispatchHelper", ctx)

        dispatcher.executeDispatch(
            frame,
            ".uno:CompareDocuments",
            "",
            0,
            (_prop("URL", original_url),),
        )

        store_args = (
            _prop("FilterName", "MS Word 2007 XML"),
            _prop("Overwrite", True),
        )
        document.storeToURL(output_url, store_args)
        document.close(True)
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
