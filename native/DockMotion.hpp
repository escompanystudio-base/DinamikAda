// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once
#include <algorithm>
#include <cmath>

namespace DinamikAdaDockMotion {
inline constexpr int revealMs = 132;
inline constexpr int concealMs = 108;
inline constexpr int hoverMs = 105;
inline constexpr int followMs = 64;
inline constexpr int releaseMs = 118;
inline constexpr double travelDip = 22.0;
inline constexpr double hiddenDip = 64.0;
inline constexpr float magnification = .06f;
inline constexpr float liftDip = 7.f;

inline double RevealStart(double offset, double opacity) {
    return opacity <= .001 ? std::min(offset, travelDip) : offset;
}

struct Sample { float scale, y, weight; };
inline Sample AtWeight(float weight) {
    return {1.f + magnification * weight, -liftDip * weight, weight};
}

inline Sample Evaluate(float distance) {
    constexpr float radius = 80.f;
    if (!std::isfinite(distance) || std::abs(distance) >= radius) return AtWeight(0);
    float weight = .5f * (1.f + std::cos(distance * 3.14159265358979323846f / radius));
    return AtWeight(weight);
}
}
