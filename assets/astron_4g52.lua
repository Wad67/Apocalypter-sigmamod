-- The Astron 2000 4G52, a 2.0 L four: the game's 2.3 L I4 retuned. Its FSMs set the engine when fitted (CheckTag),
-- its power at each condition band (Condition: 54.3 kW new, less as it wears) and its final drive.
template "astron_4g52" {
  fsm = {
    CheckTag = { finalGearRatio = 3.91, ["set:powertrain.engine.idleRPM"] = 650 },
    Condition = {
      maxPower = 42.7,
      ["condition100/SetFloatValue.floatValue"] = 54.3,
      ["condition50/SetFloatValue.floatValue"] = 38.8,
      ["condition20/SetFloatValue.floatValue"] = 19.4,
    },
    FinalDriveRatio = { ["reset/SetFsmFloat.setValue"] = 3.91 },
  },
}
