#include "../../native/DockMotion.hpp"
#include <cassert>
#include <limits>

int main() {
    using DinamikAdaDockMotion::Evaluate;
    using DinamikAdaDockMotion::RevealStart;
    assert(RevealStart(DinamikAdaDockMotion::hiddenDip, 0.0) == DinamikAdaDockMotion::travelDip);
    assert(RevealStart(11.0, .65) == 11.0);
    assert(RevealStart(0.0, 1.0) == 0.0);
    auto center = Evaluate(0);
    assert(std::abs(center.scale - 1.06f) < .0001f);
    assert(center.y == -DinamikAdaDockMotion::liftDip);
    auto left = Evaluate(-38), right = Evaluate(38), far = Evaluate(60);
    assert(left.scale == right.scale && left.y == right.y);
    assert(right.scale > far.scale && far.scale > 1);
    assert(right.scale > 1 && right.scale < 1.04f);
    assert(center.y < right.y && right.y < far.y && far.y < 0);
    for (float distance : {-10000.f, -80.f, 80.f, 10000.f, std::numeric_limits<float>::quiet_NaN()}) {
        auto at = Evaluate(distance);
        assert(at.scale == 1 && at.y == 0);
    }
    for (int x = -300; x < 300; x++) {
        auto at = Evaluate(float(x));
        assert(at.scale >= 1 && at.scale <= 1.06f);
        assert(at.y >= -DinamikAdaDockMotion::liftDip && at.y <= 0);
    }
}
