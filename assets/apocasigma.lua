-- The 1981 Mitsubishi Sigma: a game car's working parts under the Sigma's body. Its own gearbox (a four-speed's
-- ratios), a locked rear differential, rear-wheel drive.
template "sigma_chassis" {
  Rigidbody = { mass = 1115 },
  VehicleController = {
    ["powertrain.transmission.gears"] = { -3.369, 0, 3.369, 2.035, 1.36, 1.0, 0.856 },   -- reverse, neutral, 1st - 5th
    ["powertrain.differentials.0.DifferentialType"] = "Locked",
    ["powertrain.differentials.0.stiffness"] = 1,
  },
  drive = "rear",
}
