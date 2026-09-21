from auth import authenticate_user
from models import Order, User
from seed import seed_demo_data
from tests.helpers import make_test_db


def test_demo_seed_is_idempotent_and_login_ready():
    db = make_test_db()
    try:
        seed_demo_data(db)
        seed_demo_data(db)

        assert db.query(User).count() == 5
        assert db.query(Order).count() >= 4
        assert authenticate_user(db, "biuro", "1111")["name"] == "Demo Office"
        assert authenticate_user(db, "technolog", "2222")["name"] == "Demo Technologist"
        assert authenticate_user(db, "ceo", "3333")["name"] == "Demo CEO"
        first = authenticate_user(db, "produkcja", "4444")
        second = authenticate_user(db, "produkcja", "5555")
        assert first["default_shift"] == "I"
        assert second["default_shift"] == "II"
        assert first["id"] != second["id"]
    finally:
        db.close()
