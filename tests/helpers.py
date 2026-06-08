from sqlalchemy import create_engine
from sqlalchemy.orm import Session
from sqlalchemy.pool import StaticPool

from models import Base, Order, OrderStatus, Setting


TEST_USERS = {
    "office": {"id": "1", "role": "biuro", "name": "Test Office"},
    "technologist": {"id": "2", "role": "technolog", "name": "Test Technolog"},
    "director": {"id": "3", "role": "dyrektor_produkcji", "name": "Test Director"},
}


def make_test_db(settings: dict[str, str] | None = None) -> Session:
    engine = create_engine(
        "sqlite:///:memory:",
        connect_args={"check_same_thread": False},
        poolclass=StaticPool,
    )
    Base.metadata.create_all(engine)
    db = Session(engine)

    seed_settings = {"labor_rate_pln": "90.0"}
    if settings:
        seed_settings.update(settings)
    for key, value in seed_settings.items():
        db.add(Setting(key=key, value=value))
    db.commit()
    return db


def make_order(
    db: Session,
    *,
    order_number: str = "TEST-001",
    client: str = "Test Client",
    status: OrderStatus = OrderStatus.niestandard,
    **kwargs,
) -> Order:
    order = Order(
        order_number=order_number,
        client=client,
        status=status,
        **kwargs,
    )
    db.add(order)
    db.commit()
    db.refresh(order)
    return order


class FakeUpload:
    def __init__(
        self,
        name: str = "test.pdf",
        content: bytes = b"PDF",
        content_type: str = "application/pdf",
    ):
        self.filename = name
        self.content_type = content_type
        self._data = content

    async def read(self, size=-1):
        data, self._data = self._data, b""
        return data
