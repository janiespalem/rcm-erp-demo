from datetime import datetime, timezone

DEFAULT_MARGIN_PCT = 0.25

def _now():
    return datetime.now(timezone.utc).replace(tzinfo=None)
