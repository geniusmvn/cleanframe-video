
from cleanframe.engines import relative_rect_to_pixels


def test_relative_rect():
    assert relative_rect_to_pixels((0.8, 0.8, 0.1, 0.1), 1280, 720) == (
        1024, 576, 128, 72
    )


def test_clamp():
    x, y, w, h = relative_rect_to_pixels((-1, -1, 3, 3), 100, 50)
    assert x == 0 and y == 0 and w == 100 and h == 50
