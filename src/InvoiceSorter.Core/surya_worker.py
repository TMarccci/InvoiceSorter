"""Surya OCR worker for Invoice Sorter.

Loads the Surya models once (GPU if available), then reads requests from stdin - one JSON object per line:
    {"id": 1, "image": "C:\\path\\page_0001.jpg"}
and answers on stdout - one JSON object per line:
    {"id": 1, "width": 1650, "height": 2350,
     "lines": [{"text": "...", "bbox": [x0, y0, x1, y1], "confidence": 0.98, "words": [{"text": "...", "bbox": [...]}]}]}
The first line written is {"ready": true, "device": "cuda", "version": "0.17.1"} (or {"error": "..."}).
Everything else (progress bars, library messages) goes to stderr. The worker ends when stdin is closed.

Requires surya-ocr 0.17.x (pure PyTorch: text detection + recognition run in this process; nothing is sent anywhere).
"""
import io
import json
import re
import sys

# The protocol is always UTF-8 (Hungarian accents), whatever the console code page is.
protocol = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", newline="\n")   # only protocol messages go here
requests = io.TextIOWrapper(sys.stdin.buffer, encoding="utf-8")
sys.stdout = sys.stderr        # library prints must not corrupt the protocol

TAG = re.compile(r"</?[a-zA-Z][^>]*>")


def send(obj):
    protocol.write(json.dumps(obj, ensure_ascii=False) + "\n")
    protocol.flush()


def clean(text):
    return TAG.sub("", text or "").replace("\u00a0", " ").strip()


def main():
    try:
        import importlib.metadata as md
        import torch
        from PIL import Image
        from surya.detection import DetectionPredictor
        from surya.foundation import FoundationPredictor
        from surya.recognition import RecognitionPredictor

        foundation = FoundationPredictor()
        detector = DetectionPredictor()
        recognizer = RecognitionPredictor(foundation)
        device = "cuda" if torch.cuda.is_available() else "cpu"
        send({"ready": True, "device": device, "version": md.version("surya-ocr")})
    except Exception as e:  # noqa: BLE001 - report any startup problem to the app
        send({"error": f"{type(e).__name__}: {e}"})
        return 1

    for raw in requests:
        raw = raw.strip()
        if not raw:
            continue
        request_id = 0
        try:
            request = json.loads(raw)
            request_id = request.get("id", 0)
            image = Image.open(request["image"]).convert("RGB")
            result = recognizer([image], det_predictor=detector, math_mode=False, return_words=True, sort_lines=True)[0]
            lines = []
            for line in result.text_lines:
                text = clean(line.text)
                if not text:
                    continue
                words = [{"text": clean(w.text), "bbox": list(w.bbox)}
                         for w in (line.words or []) if clean(w.text)]
                lines.append({"text": text, "bbox": list(line.bbox),
                              "confidence": float(line.confidence or 0), "words": words})
            send({"id": request_id, "width": image.width, "height": image.height, "lines": lines})
        except Exception as e:  # noqa: BLE001 - one bad page must not stop the worker
            send({"id": request_id, "error": f"{type(e).__name__}: {e}"})
    return 0


if __name__ == "__main__":
    sys.exit(main())
