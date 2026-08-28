# Outdoor Telemetry: Weather, Sun, Rain, and Air Quality

## The ask

> I want to gather wider climate data from outside than a simple hygrometer can provide. I want you to do some product research and recommend me a weather station and/or system.
>
> Need:
> - Sunlight measurement. Our sunroom is full of windows and is hot on sunny days year round, cold on cloudy days year round. I want to be able to detect sun levels so that we can train an HVAC response to the sun level input. We also live in a clearing in a wooded area, so we might need to deploy multiple units to get a clear reading?
> - Wind level/direction/trend
> - Rain detection - we have skylights and it would be nice to get a notification to close them
> - UV levels and any other health-related telemetry is available
> - Integration with HomeAssistant and/or accessible API that aerie can integrate with.
> - If we just need a single weather station, we can use wifi, ethernet, or power-over-eithernet. i will be putting up an IT pole outside that will be easy to mount a station on and it will have PoE wired up already. if we need a distributed system, i don't want to run PoE all over my property.
> - A lot of those stations come with LED telemetry readouts, i do not need one of those as we will be integrating the data into our dashboards

## Summary

The ask reads as "recommend a weather station," but three of the five requirements are not weather-station problems:

- **Sunlight for HVAC training** is a *shading geometry* problem. A pole-mounted sensor measures irradiance at the pole; the sunroom's heat gain depends on which windows the tree line is blocking at this azimuth. Adding more weather stations does not converge on the answer. Measuring light **at the sunroom glass** does, for about $20 a point.
- **Rain for skylights** is a *presence-detection* problem with a latency requirement. Every weather-station rain gauge measures accumulation; the fastest of them still answers a question you did not ask. A purpose-built optical rain sensor answers "is it raining right now" in seconds, and it is the one part of this build that actually uses the PoE on the pole.
- **Air quality** is the requirement hiding inside "any other health-related telemetry," and it is the one with the most product surface and the most control-loop value. It gets its own section below, because it is not an accessory to the weather station — it is a second sensing system that happens to share a gateway.

What is left — wind, UV, outdoor irradiance, barometric trend, outdoor temp/RH — is a genuine weather-station job, and one station on the pole covers it.

**Recommended system: Ecowitt GW3000 gateway + WS90 array, a Hydreon RG-9 on the pole, an AirGradient ONE indoors, an Ecowitt WH41 outdoors, and a small fleet of ESPHome nodes in the sunroom.** Roughly $690 all-in, everything local, and — the part that matters for this codebase — **zero new ingest architecture**, because [`device-architecture.md`](../device-architecture.md) already generalized the shape these sensors arrive in.

---

## 1. The sun requirement, reframed

### Why one pole sensor is not enough, and why five would not help

The goal is stated precisely in the ask: *train an HVAC response to the sun level input*. That means the sun signal is a **feature in a model**, not a display value. It needs to correlate with sunroom heat gain, and it needs to *lead* the temperature rise, or it adds nothing over the thermostat already in the room.

A pyranometer on the pole measures global horizontal irradiance at the pole. In a clearing in the woods, the pole and the sunroom disagree constantly — and correctly. The pole may be in full sun while the sunroom's west glass is behind a treeline at 250° azimuth. That is not sensor error to be averaged away with more units; it is geometry. Buying three weather stations to triangulate it is spending $450 to approximate a signal that can be measured directly.

### Measure the light at the glass

ESPHome nodes with a **VEML7700** ambient light sensor, placed inside the sunroom on different exposures (east, south, west).

- **VEML7700 over BH1750.** BH1750 tops out at 65,535 lx; direct sun through glass exceeds 100,000 lx, so a BH1750 clips exactly when the reading matters most. VEML7700 covers ~0–120,000 lx. TSL2591 also works but is fussier about gain switching.
- **Indoors means cheap.** No weatherproofing, no batteries, no RF link, no radiation shield. USB power and WiFi. ~$10 for the sensor, ~$6 for an ESP32-C3.
- **ESPHome means native HA.** No integration to write, no cloud.

### The feature set this produces

| Signal | Source | Cost | What the model learns from it |
|---|---|---|---|
| Interior lux, per exposure | VEML7700 nodes | ~$20/point | The actual heat-gain driver, leading zone temperature by 20–40 min |
| Outdoor irradiance (W/m²) | WS90 | included | The reference — "how sunny is it, absent shading" |
| Sun azimuth + elevation | HA `sun.sun` | **free** | Where the sun is, exactly, with no hardware at all |

The ratio `interior_lux / outdoor_irradiance`, indexed by azimuth and elevation, **is** the tree line. The model learns the shading map without anyone surveying it, and re-learns it when the leaves come off. That is a better outcome than any arrangement of outdoor sensors would produce, and it is cheaper.

The 20–40 minute lead is the whole justification for the build. Reacting to sunroom temperature is something the existing Mysa already supports. Reacting to sunroom *light* is predictive control.

### Placement

Three nodes: one on each of the sunroom's major exposures. Mount on the interior wall or sill facing outward, out of direct beam if possible — a sensor in the beam saturates and stops discriminating; a sensor reading the room's diffuse level tracks total gain better. Worth one round of iteration after the first sunny week of data.

---

## 2. Rain: buy the purpose-built part

Skylight protection and rainfall measurement are different jobs with different latency budgets.

| Mechanism | Detection latency | Used by |
|---|---|---|
| Tipping bucket | Needs 0.01" to accumulate — minutes of rain | WS69, Ambient, Davis |
| Piezo / haptic | First drops register | WS90, Tempest |
| **Optical presence** | **Seconds** | **Hydreon RG-9** |

The [Hydreon RG-9](https://rainsensors.com/products/rg-9/) (~$60–80) is built for exactly the "is it raining right now" question. IR beam off a curved lens, sensitivity selectable down to **Rain Drops**, and selectable hold times (5/10/15 min) so a brief gap between showers does not flap the automation. ESPHome supports it natively via [`hydreon_rgxx`](https://esphome.io/components/sensor/hydreon_rgxx/) over UART, with the OUT pin also readable as a GPIO binary sensor.

**This is what the PoE pole is for.** PoE splitter → 5V → ESP32 → RG-9 over UART. The weather station itself will not use PoE (see §4), so without the rain sensor the pole's PoE goes unused by this project.

Keep the WS90's piezo gauge anyway — it answers "how much rain fell," which is the useful long-horizon number. The RG-9 answers "close the skylights." Two sensors, two questions.

---

## 3. Air quality — a first-class system, not an accessory

### Why this deserves top billing

Air quality is the only telemetry here that is directly a **health** measurement rather than a comfort input, and it is the only one that turns the skylights from a liability into an actuator. Three questions, three sensors, one control loop:

| Question | Sensor | Where |
|---|---|---|
| **Do I need to bring outside air in?** | CO₂ (NDIR) | Indoors, per occupied zone |
| **Is it safe to bring outside air in?** | PM2.5/PM10 | Outdoors |
| **Is my filtration actually working?** | Indoor PM ÷ outdoor PM | Both |

That third row is the one people skip and it is the most interesting. The indoor/outdoor PM ratio is a direct, continuous measurement of the house's filtration effectiveness. It tells you whether the furnace filter is doing anything, when it is loaded, and whether a portable purifier earns its power draw. No single-sensor deployment can produce it.

### It closes the loop with the skylights

The rain sensor tells the system when to **close** the skylights. Air quality tells it when to **open** them:

```
indoor CO2 high  AND  outdoor PM low  AND  not raining  AND  outdoor temp in band
    -> ventilate (open skylights, run fans)

outdoor PM high (wood smoke, wildfire drift)
    -> seal and recirculate, regardless of CO2
```

This is a natural extension of [`climate-brain-architecture.md`](../climate-brain-architecture.md), which today optimizes a single objective (comfort temperature) against two actuator classes (AC, fans). Air quality adds a second objective and a third actuator class. The existing structure absorbs it: ventilation openings are Devices with `ReadWrite` channels, governed by an `EfActuatorPolicy` row, scored by the same controller.

### Specific to a wooded property

- **Wood smoke** — neighbors' stoves and your own fireplace. Seasonal PM2.5 spikes, often overnight, often worse indoors than out.
- **Wildfire smoke drift** — multi-day regional events. The single most valuable thing an outdoor PM sensor tells you, because the decision it drives (seal the house) is one you would otherwise make from a news headline a day late.
- **Pollen and mold** — trees. **Not measurable by any consumer sensor.** This is an API (Google Pollen, Ambee, Breezometer), not hardware. Worth saying plainly so nobody buys a "pollen sensor."
- **Radon** — soil and rock. See the tiering below; this one is a decision, not a dashboard.

### Recommended air quality build

**Outdoor — Ecowitt WH41 PM2.5, $59.99.** Solar-powered, weatherproof, and it joins the **same GW3000 gateway** as the weather array. No second integration, no second app, no second failure mode. This is the strongest argument for choosing Ecowitt over Tempest: the ecosystem is modular, and outdoor air quality is the first thing you will want to add.

**Indoor — [AirGradient ONE](https://www.airgradient.com/indoor/), $230 assembled / $138 as a kit.** The best local-only indoor monitor available:

- **True NDIR CO₂** (±50 ppm over 400–5000 ppm) — not eCO₂ derived from a VOC sensor. This distinction matters more than any other spec on this page; eCO₂ is a guess dressed as a measurement, and most cheap "CO₂ monitors" ship it.
- PM0.3/1/2.5/10 via Plantower PMS5003T
- TVOC and NOx index (SGP41)
- Temp/RH
- **Native [HA integration](https://www.home-assistant.io/integrations/airgradient/)** since HA 2024.6, local, no cloud
- **Open source hardware and firmware** — flashable with ESPHome if you ever want to bypass their firmware entirely

Put the first one where occupancy-hours are highest. A bedroom is the strongest CO₂ story (overnight CO₂ in a closed bedroom routinely passes 1500 ppm and measurably degrades sleep). The sunroom is the strongest *research* story, since it is the zone already under study.

**Scaling out — do not buy five AirGradients.** At $230 each that is not the right per-zone answer. Two better paths:

1. **Ecowitt WH45** ($89.99) — 5-in-1 CO₂/PM2.5/PM10/temp/RH, indoor, on the gateway you already have. Cheapest way to add a second CO₂ point with no new integration.
2. **DIY on the nodes you are already building** — an ESP32 carrying an **SCD41** (photoacoustic NDIR CO₂, ~$25) alongside the VEML7700 you are installing in the sunroom anyway. One node = lux + CO₂ + temp/RH for ~$60. Add an SEN55 (~$45) if you want PM at that point too. This is the same ESPHome build already required by §1, so the marginal effort is one line of YAML per sensor.

That symmetry is worth leaning on: **the sunroom light nodes and the air quality nodes are the same nodes.**

**Radon — measure once, then decide.** Radon is a slow-moving hazard whose only response is mitigation (a fan and a pipe). It is not a control input and does not belong on a live dashboard.

- Start with a **short-term test kit ($15–25)**. If the result is well under 2 pCi/L, you are done.
- If it is elevated, a **RadonEye RD200** (~$150, BLE) gives continuous readings to verify a mitigation system is working. No core HA integration, but two live paths: the [`radon_eye_ble` ESPHome component](https://esphome.io/components/sensor/radon_eye_ble/) (which also means an ESP32 you already own can be the Bluetooth bridge) or the HACS integration [`jdeath/rd200v2`](https://github.com/jdeath/rd200v2). Prefer the ESPHome route — it keeps the sensor on the same node fleet as everything else in this build rather than adding a HACS dependency.
- **Airthings View Plus** (~$300) bundles radon with PM/CO₂/VOC and has a local BLE integration, but you are paying twice for sensors the AirGradient already covers better. Skip it.
- **Ecosense EcoQube** ($170) is WiFi with IFTTT — cloud-shaped, no local API. Wrong fit for this stack.

### Honest caveats on cheap AQ sensors

State these in the UI eventually, because they will otherwise generate false alarms:

- **Optical PM sensors over-read in high humidity.** Plantower-class nephelometers count water droplets as particles. Outdoor PM in fog, drizzle, or dew will read high and it is not smoke. The EPA has published a [correction for exactly this](https://www.mdpi.com/1424-8220/22/24/9669) on PurpleAir data; AirGradient applies a similar humidity compensation. Expect your raw outdoor WH41 to be noisier than the corrected indoor AirGradient.
- **VOC and NOx are *indices*, not concentrations.** SGP41 outputs a value relative to its own rolling baseline. Useful for "something changed in this room"; meaningless as an absolute health number. Do not put a ppb unit on it.
- **Consumer ozone and formaldehyde sensors are not trustworthy.** Cross-sensitive, drifty, and usually just the VOC sensor relabeled. Do not buy a device advertising them as a differentiator.
- **NDIR CO₂ needs fresh-air calibration.** Both SenseAir and SCD41 self-calibrate by assuming they see ~400 ppm at some point in a rolling window. A sensor in a room that is *never* ventilated will slowly baseline itself wrong. Worth knowing before trusting a bedroom trend over months.

### Rejected: PurpleAir

PurpleAir Zen/Flex (~$250–300) is more accurate than the WH41, uses dual lasers so channel A/B disagreement flags a failing sensor, has a [documented local JSON API](https://community.purpleair.com/t/pm2-5-data-differs-between-json-and-api/11383), and contributes to the public map. It is a genuinely better outdoor PM sensor. It is also 4–5× the price and a separate integration, and for the decision it drives — "seal the house or ventilate" — the WH41's accuracy is sufficient. Revisit if wildfire smoke turns out to be a recurring multi-week reality at this location.

---

## 4. The weather station: Ecowitt GW3000 + WS90

| Why | Detail |
|---|---|
| Covers every remaining requirement | WS90 is 7-in-1: temp, humidity, **ultrasonic** wind speed/direction, **piezo** rain, **UV index**, **solar radiation** |
| Local push to HA | The [HA Ecowitt integration](https://www.home-assistant.io/integrations/ecowitt/) is `local_push` — the gateway POSTs to an endpoint on HA. No cloud, no polling |
| Direct Aerie path exists | The gateway supports **custom server** upload targets, so it can POST to `Aerie.Api` in parallel (see §7) |
| Headless by default | The gateway **is** the no-display option. No console to buy and ignore — this was an explicit requirement, and Ambient/Davis both push you toward a console |
| Modular | Outdoor PM, lightning, indoor CO₂, extra temp/RH all land on the same gateway with the same integration |
| Ethernet | GW3000 has a LAN port and a microSD slot for local logging |
| No moving parts on the array | Ultrasonic anemometer and piezo rain — no bearings to seize, no bucket to clog with pine needles. On a wooded property this matters |

**PoE caveat:** the GW3000 is 5V USB-C, indoor-rated, **no PoE**. It lives inside near a switch; the WS90 reaches it over 915 MHz RF. The pole carries the array and the RG-9 rain sensor — the RG-9 is what consumes the PoE drop.

**HTTP-only caveat:** Ecowitt gateways do not support TLS. Both the HA callback and any direct-to-Aerie feed must be plain HTTP on the LAN. Fine on-LAN, but it means the gateway cannot post through the ingress — see §7.

**On solar radiation accuracy:** Ecowitt's "solar radiation" is a lux sensor with a fixed lux→W/m² conversion, not a cosine-corrected pyranometer. For training an HVAC response this is fine — it is monotonic with true irradiance, and the model is learning a relationship, not reporting a calibrated number. Do not publish these figures as science.

### Rejected alternatives

**Ambient Weather WS-5000 / WS-2902** ($185–190). Cheap, popular, correct sensor suite. But the [HA integration is cloud-only](https://www.home-assistant.io/integrations/ambient_station/) — it polls Ambient Weather Network. Disqualifying for a self-hosted stack that is explicitly trying to own its own data.

**WeatherFlow Tempest** (~$339). Genuinely elegant: one sealed unit, no moving parts, [local UDP broadcast](https://www.home-assistant.io/integrations/weatherflow/) that HA reads without cloud, built-in lightning detection, instant haptic rain. Rejected on three counts: 2× the price; the haptic gauge [under-reports 20–30% in heavy rain](https://theweatherstationexperts.com/weatherflow-tempest-weather-system-review/) and misses fine mist; and it is monolithic — one sensor failure replaces the whole unit, and there is no path to add outdoor PM to it. Pick it anyway if a single clean install is worth more than modularity.

**Davis Vantage Pro2 Plus** (~$1000+ with the UV/solar option). The only option here with a [true pyranometer](https://www.davisinstruments.com/products/solar-radiation-sensor), and WeatherLink Live has a documented local JSON API. Correct answer if the goal were research-grade irradiance. The goal is a model feature. Not worth 6×.

**Budget variant worth knowing about:** the [GW3002 bundle](https://shop.ecowitt.com/products/gw3002) is the GW3000 plus a WS69 array for **$118.99**, and the WS69 still reports solar radiation and UV. You give up piezo rain and ultrasonic wind for a tipping bucket and spinning cups. Since a dedicated RG-9 is handling rain *detection* anyway, this trade is more defensible than it first looks — it saves $85 and costs you moving-part longevity. Given the wooded siting, I still recommend the WS90.

---

## 5. Siting

Mounting on an IT pole beside the house, in a clearing, is a compromise on every channel. Worth writing down now so nobody later mistakes siting error for sensor fault:

- **Wind** — obstructed by the house and the treeline. Standard siting is 10 m in the open. The readings will be directionally biased and low. Fine as a **trend** signal, which is what was asked for. Do not compare the absolute numbers to the airport.
- **Temperature** — the radiation shield must be genuinely shaded, away from the wall, and away from any pavement or roof that re-radiates. A pole-mounted shield in afternoon sun reads several degrees high.
- **Solar / UV** — will be tree-shaded early and late in the day. This is another reason the interior lux sensors carry the HVAC job and the pole carries only the reference.
- **Rain gauge** — must be level and outside any tree drip line. A gauge under a branch reports a downpour twenty minutes after the rain stops.
- **Outdoor PM (WH41)** — away from the driveway, the grill, and any dryer vent. Under an eave is fine and keeps rain off the intake.
- **RG-9** — needs sky exposure and a slight tilt so the lens sheds water. Do not put it under the eave with the PM sensor.

---

## 6. Shopping list

### Core build

| Item | Price | Job |
|---|---|---|
| Ecowitt WS90 7-in-1 array | $149.99 | Wind, rain accumulation, temp/RH, **UV + solar radiation** |
| Ecowitt GW3000 gateway | $54.99 | Ethernet, HTTP API, SD logging, local push to HA |
| Hydreon RG-9 + ESP32 (PoE-split) | ~$85 | **Instant rain → skylight alert** |
| AirGradient ONE (assembled) | $230 | Indoor NDIR CO₂, PM, VOC/NOx |
| Ecowitt WH41 outdoor PM2.5 | $59.99 | Outdoor PM — the ventilate/seal decision |
| Sunroom node: ESP32-C3 + VEML7700 + SCD41 | ~$65 | Lux + CO₂ at the zone under study |
| 2× lux-only nodes (ESP32-C3 + VEML7700) | ~$40 | East/west exposures |
| **Total** | **~$685** | |

### Optional, same gateway, no new integration

| Item | Price | Job |
|---|---|---|
| Ecowitt WH57 lightning detector | $55.99 | Strike distance — a decent "get off the deck" notification |
| Ecowitt WH45 indoor 5-in-1 | $89.99 | Cheap second CO₂/PM point |
| Ecowitt WH31 multi-channel temp/RH | ~$25 ea | Up to 8 extra zone points, battery, 915 MHz |
| Radon short-term test kit | $15–25 | Answer the radon question once |
| SEN55 add-on for a sunroom node | ~$45 | Indoor PM for the I/O filtration ratio |

### Cost-down options

- AirGradient ONE **kit** is $138 instead of $230 — it is a solder-free assembly, roughly 20 minutes.
- GW3002 bundle at $118.99 replaces the GW3000 + WS90 line items ($205) if you accept the WS69 array.

---

## 7. How this lands in Aerie

The good news: **[`device-architecture.md`](../device-architecture.md) already did the hard part.** Every sensor above arrives as one or more HA entities, and a `DeviceChannel` is by definition "one (HA entity, attribute) pair mapped to one metric." Adding a weather station is adding *rows*, not architecture — exactly what that document set out to make possible.

### 7.1 New metrics

`DeviceChannelMetric` in [`Ef/DeviceMapping.cs`](../../src/Aerie.Api/Ef/DeviceMapping.cs) is documented append-only (the column stores the underlying int). Append:

```csharp
public enum DeviceChannelMetric
{
    // ... existing 13 values, unchanged ...

    // Weather station
    Illuminance,          // lx  - interior light sensors (VEML7700)
    SolarRadiation,       // W/m2
    UvIndex,
    WindSpeed, WindGust, WindDirection,   // mph, mph, degrees
    RainRate,             // in/hr
    RainAccumulation,     // in
    BarometricPressure,   // inHg

    // Air quality
    Pm25, Pm10,           // ug/m3
    Co2,                  // ppm - NDIR only; never map an eCO2 entity here
    VocIndex, NoxIndex,   // unitless index, NOT a concentration
    Radon,                // pCi/L

    // Non-numeric
    RainState,            // "raining" / "dry" -> EfStateChange, not EfMeasurement
}
```

Every one of these is `Direction = Read`. All but `RainState` land in `EfMeasurement.Value` (decimal) with no schema change at all; `RainState` lands in `EfStateChange` via the existing `ChannelValueExtractor` fallback. **The migration is an enum edit.**

`DeviceKind` gains `WeatherStation, AirQuality, LightSensor, RainSensor`. `Kind` is nullable and only drives discovery suggestions, so this is cosmetic — but it makes the admin Discovery page able to propose a sensible channel set when a WS90 shows up.

### 7.2 Ingest — three paths, and which to use

**Path A — everything through HA. Use this.** Ecowitt gateway → HA local push. AirGradient → native HA integration. ESPHome nodes → native HA. All of them become HA entities, and [`Jobs/SampleChannels.cs`](../../src/Aerie.Api/Jobs/SampleChannels.cs) picks them up with **no new code** — they are additional `EfDeviceChannel` rows pointing at new `HaEntityId`s, grouped and written by the same loop that already handles Mysa thermostats and hygrometers. Adding a sensor becomes a form in the admin UI. This is the payoff for having built the device-mapping layer.

**Path B — the fast path for rain.** `SampleChannels` runs on `TimeSpan.FromMinutes(1)` and reads HA *history*. A skylight alert cannot wait a minute plus a history-write round trip. There is already a precedent for exactly this shape in the codebase: `HomeAssistantEventListener` → `IMotionEventDispatcher` → SSE, surfaced by [`MotionEventsController`](../../src/Aerie.Api/Controllers/MotionEventsController.cs). Rain onset is structurally identical — an HA state change that must reach the kiosk and the notification path *now*.

Add a `RainEventDispatcher` fed by the same listener, with the one-minute sampler still writing `RainState` history for the record. Reuses the per-replica dispatcher pattern already documented in [`camera-devices-architecture.md`](../camera-devices-architecture.md), including its "every replica holds its own HA subscription" property.

**Path C — Ecowitt custom-server direct to Aerie. Build the hook, do not use it yet.** The gateway can POST to an arbitrary HTTP endpoint in Ecowitt/Wunderground protocol. A `WeatherIngestController` in the shape of [`UiLogsController`](../../src/Aerie.Api/Controllers/UiLogsController.cs) (`[ApiController]`, POST, same-origin/LAN so no CORS) could write straight to `EfMeasurement`.

**Recommend against shipping this in v1.** It is a second source of truth for data Path A already delivers, and reconciling two writers into the unique `(ChannelId, Timestamp)` index is a problem you do not have yet. Keep it as a documented escape hatch for two cases: if sub-minute solar-radiation resolution turns out to matter for the thermal model, or if HA becomes an availability bottleneck for outdoor data.

If it is ever built, note the constraint from §4: **Ecowitt cannot do TLS**, so this endpoint must be reachable over plain HTTP on the LAN. It cannot live behind the ingress alongside everything else, which is itself a reason to prefer Path A.

### 7.3 Climate brain

[`climate-brain-architecture.md`](../climate-brain-architecture.md) absorbs this with one conceptual addition and one hard rule.

**Solar gain is a disturbance term, not an actuator.** The controller's prediction becomes `predicted_zone_temp = f(current, actuator_action, solar_forecast)`, where `solar_forecast` is driven by interior illuminance at `t-30min` plus sun position. This changes the prediction function, not the control structure — candidates are still enumerated from `AvailableOptions`, still scored, still clamped by `EfActuatorPolicy`. The Phase 7 "replace seeded gains with fitted ones" story extends naturally: solar gain per zone per exposure is exactly the kind of parameter that starts as a guess with `Source = Manual` and gets overwritten with `Source = Fitted`.

**Ventilation is a new actuator class, and rain is an interlock — not a score.** If skylights ever become motorized, they are Devices with a `ReadWrite` position/power channel and an `EfActuatorPolicy` row, and the controller may score opening them against comfort and CO₂ like anything else. But the rain signal must be a **hard clamp in the policy layer**, evaluated before scoring, never a weighted term in the objective.

A controller that is allowed to trade "the rug gets wet" against "cheaper cooling" will eventually get the rug wet — that is not a bug in the weights, it is what an optimizer does when you hand it a cost it can pay. This is the same fail-closed instinct already encoded in "absent `EfActuatorPolicy` row means the controller will not touch the device."

**Air quality as a second objective.** CO₂ over threshold, indoor PM over threshold, and the I/O ratio all become scoreable terms. The interesting one is the coupling: ventilation reduces CO₂ but imports outdoor PM and outdoor temperature. That is a genuine three-way trade and it is exactly what the existing candidate-scoring structure is for.

### 7.4 Dashboard

The Outside zone (`ZoneKind.Outside`) already exists as a first-class Zone, and `WeatherService` already sources its card from device channels. Wind, UV, solar radiation, and outdoor PM are new channels on devices assigned to that zone — the card gets richer with no re-plumbing.

New surfaces worth their own design pass:
- **Sun/shading view** — interior lux per exposure over the day, overlaid on outdoor irradiance. This is the artifact that will actually show whether the tree-line hypothesis holds.
- **Air quality strip** — indoor CO₂ per zone, indoor vs outdoor PM, and the I/O ratio as a filtration health indicator.
- **Rain state** on the kiosk, wired to the SSE path from §7.2, with the skylight reminder.

---

## 8. Suggested build order

Each phase is independently useful, which matters because the sun data needs a season to be interesting and the air quality data is useful on day one.

1. **Gateway + WS90.** Prove local push into HA, map the channels in the admin UI, watch `SampleChannels` pick them up unmodified. Lowest-risk validation that the device-mapping layer generalizes as claimed.
2. **RG-9 on the pole.** First automation with a user-visible payoff. Includes the SSE fast path (§7.2) and a notification.
3. **Air quality — WH41 outdoors + AirGradient ONE indoors.** Immediately useful, no model training required, and produces the I/O ratio from the first day.
4. **Sunroom ESPHome nodes.** Start collecting the light data. It does nothing for months; start it early for that reason.
5. **Solar gain in the climate brain.** Once there is a season of lux-vs-temperature data, add the disturbance term and fit it. Runs in shadow mode first, per the existing plan's own discipline.
6. **Ventilation control.** Only if the skylights get motorized. Rain interlock first, then scoring.

---

## 9. Open questions

- **Are the skylights motorized, or is v1 notification-only?** This is the biggest scope fork in the document — it decides whether §7.3's ventilation section is real or hypothetical.
- **Where is the sunroom relative to the pole, and what is the treeline azimuth from each?** Determines whether the pole's solar reading is a usable reference or so shaded as to be worthless.
- **Is there an HA Bluetooth proxy on the network?** Gates the BLE options (RadonEye, Aranet4) if radon turns out to be elevated.
- **Wood smoke or wildfire smoke — which is the actual driver?** If it is recurring multi-week wildfire drift, PurpleAir's accuracy starts to justify its price. If it is neighbors' stoves, the WH41 is plenty.
- **Which room gets the first AirGradient?** Bedroom optimizes for the health story (sleep CO₂); sunroom optimizes for the research story (one zone fully instrumented).
