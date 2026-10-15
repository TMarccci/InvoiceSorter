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
import os
import re
import sys

# The protocol is always UTF-8 (Hungarian accents), whatever the console code page is.
protocol = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", newline="\n")   # only protocol messages go here
requests = io.TextIOWrapper(sys.stdin.buffer, encoding="utf-8")
sys.stdout = sys.stderr        # library prints must not corrupt the protocol

TAG = re.compile(r"</?[a-zA-Z][^>]*>")

# Larger images are scaled down before reading (a tall till receipt rendered at 1300 x 6500 px takes several times
# longer than an A4 page); the coordinates in the answer are scaled back to the original image.
MAX_PIXELS = int(os.environ.get("INVOICESORTER_SURYA_MAX_PIXELS", "4500000"))


def configure_batch_sizes(torch):
    """Surya's GPU defaults (recognition 256, detection 32) are meant for 24+ GB cards. On a smaller card Windows does
    not fail but swaps video memory into system RAM, which makes every page many times slower. Values set in the
    environment (RECOGNITION_BATCH_SIZE, DETECTOR_BATCH_SIZE) are kept."""
    if not torch.cuda.is_available() or not os.environ.get("TORCH_DEVICE", "cuda").startswith("cuda"):
        return None
    gb = torch.cuda.get_device_properties(0).total_memory / 1024 ** 3
    recognition, detection = (32, 4) if gb <= 8.5 else (64, 8) if gb <= 12.5 else (128, 16) if gb <= 16.5 else (None, None)
    if recognition is not None:
        os.environ.setdefault("RECOGNITION_BATCH_SIZE", str(recognition))
        os.environ.setdefault("DETECTOR_BATCH_SIZE", str(detection))
    return {"vram_gb": round(gb, 1),
            "recognition_batch": os.environ.get("RECOGNITION_BATCH_SIZE", "alapértelmezett"),
            "detector_batch": os.environ.get("DETECTOR_BATCH_SIZE", "alapértelmezett")}


def send(obj):
    protocol.write(json.dumps(obj, ensure_ascii=False) + "\n")
    protocol.flush()


def clean(text):
    return TAG.sub("", text or "").replace("\u00a0", " ").strip()


def main():
    try:
        import importlib.metadata as md
        import torch
        batch = configure_batch_sizes(torch)   # before surya reads its settings
        from PIL import Image
        from surya.detection import DetectionPredictor
        from surya.foundation import FoundationPredictor
        from surya.recognition import RecognitionPredictor

        foundation = FoundationPredictor()
        detector = DetectionPredictor()
        recognizer = RecognitionPredictor(foundation)
        from surya.settings import settings
        device = settings.TORCH_DEVICE_MODEL
        if batch:
            print(f"Surya GPU: {torch.cuda.get_device_name(0)}, {batch}", file=sys.stderr, flush=True)
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
            width, height = image.width, image.height
            scale = 1.0
            if width * height > MAX_PIXELS:
                scale = (MAX_PIXELS / (width * height)) ** 0.5
                image = image.resize((max(1, int(width * scale)), max(1, int(height * scale))), Image.LANCZOS)
            result = recognizer([image], det_predictor=detector, math_mode=False, return_words=True, sort_lines=True)[0]

            def box(b):
                return [float(v) / scale for v in b]

            lines = []
            for line in result.text_lines:
                text = clean(line.text)
                if not text:
                    continue
                words = [{"text": clean(w.text), "bbox": box(w.bbox)}
                         for w in (line.words or []) if clean(w.text)]
                lines.append({"text": text, "bbox": box(line.bbox),
                              "confidence": float(line.confidence or 0), "words": words})
            send({"id": request_id, "width": width, "height": height, "lines": lines})
        except Exception as e:  # noqa: BLE001 - one bad page must not stop the worker
            send({"id": request_id, "error": f"{type(e).__name__}: {e}"})
    return 0


if __name__ == "__main__":
    sys.exit(main())
