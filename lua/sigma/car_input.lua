-- The game's driving controls: its cars' DriveTrigger/INPUT FSMs, written out by SigmaMod's tools/car_input.py from
-- Outrider.fsm.json. Generated: edit car_input.py rather than this. Used by parts.car_input.
--   o.horn: the vehicle's horn (an object with an AudioSource)
return function(o)
  return {
    INPUT_AxisInput = {
      variables = {
        float = {
          Throttle = 0,
          Brakes = 0,
          Clutch = 0,
          cruise = 0,
        },
        gameObject = {
          DriveTrigger = null,
          Car = null,
        },
        object = {
          VehicleController = {
            type = "NWH.VehiclePhysics2.VehicleController",
            value = null,
          },
        },
      },
      start = "input",
      states = {
        input = {
          actions = { {
            GetAxis = {
              axisName = "Throttle",
              multiplier = 1,
              invert = false,
              store = "{Throttle}",
              everyFrame = true,
            },
          }, {
            GetAxis = {
              axisName = "Brakes",
              multiplier = 1,
              invert = false,
              store = "{Brakes}",
              everyFrame = true,
            },
          }, {
            GetAxis = {
              axisName = "Clutch",
              multiplier = 1,
              invert = false,
              store = "{Clutch}",
              everyFrame = true,
            },
          }, {
            GetParent = {
              gameObject = "owner",
              storeResult = "{DriveTrigger}",
            },
          }, {
            GetParent = {
              gameObject = "{DriveTrigger}",
              storeResult = "{Car}",
            },
          }, {
            GetComponent = {
              gameObject = "{Car}",
              storeComponent = "{VehicleController}",
              everyFrame = false,
            },
          }, {
            GetFsmFloat = {
              gameObject = "owner",
              fsmName = "INPUT_CruiseControl",
              variableName = "cruise",
              storeValue = "{cruise}",
              everyFrame = true,
            },
          }, {
            FloatOperator = {
              float1 = "{Throttle}",
              float2 = "{cruise}",
              operation = "Max",
              storeResult = "{Throttle}",
              everyFrame = true,
            },
          }, {
            SetProperties = {
              on = "{VehicleController}",
              ["input.Throttle"] = "{Throttle}",
              everyFrame = true,
            },
          }, {
            SetProperties = {
              on = "{VehicleController}",
              ["input.Brakes"] = "{Brakes}",
              everyFrame = true,
            },
          }, {
            SetProperties = {
              on = "{VehicleController}",
              ["input.Clutch"] = "{Clutch}",
              everyFrame = true,
            },
          } },
        },
      },
    },
    INPUT_AxisSteering = {
      variables = {
        float = {
          Steering = 0,
        },
        gameObject = {
          Car = null,
          DriveTrigger = null,
        },
        object = {
          VehicleController = {
            type = "NWH.VehiclePhysics2.VehicleController",
            value = null,
          },
        },
      },
      start = "input",
      states = {
        input = {
          actions = { {
            GetAxis = {
              axisName = "Steering",
              multiplier = 1,
              invert = false,
              store = "{Steering}",
              everyFrame = true,
            },
          }, {
            GetParent = {
              gameObject = "owner",
              storeResult = "{DriveTrigger}",
            },
          }, {
            GetParent = {
              gameObject = "{DriveTrigger}",
              storeResult = "{Car}",
            },
          }, {
            GetComponent = {
              gameObject = "{Car}",
              storeComponent = "{VehicleController}",
              everyFrame = false,
            },
          }, {
            SetProperties = {
              on = "{VehicleController}",
              ["input.Steering"] = "{Steering}",
              everyFrame = true,
            },
          } },
        },
      },
    },
    INPUT_ShiftUpDown = {
      variables = {
        gameObject = {
          Car = null,
          DriveTrigger = null,
          GearChangeSound = null,
        },
        object = {
          VehicleController = {
            type = "NWH.VehiclePhysics2.VehicleController",
            value = null,
          },
          audio = {
            type = "UnityEngine.AudioClip",
            value = null,
          },
        },
      },
      start = "shift",
      states = {
        shift = {
          actions = { {
            GetButtonDown = {
              buttonName = "ShiftUp",
              sendEvent = "up",
              storeResult = "{}",
            },
          }, {
            GetButtonDown = {
              buttonName = "ShiftDown",
              sendEvent = "down",
              storeResult = "{}",
            },
          } },
          transitions = {
            up = "UP",
            down = "DOWN",
          },
        },
        UP = {
          actions = { {
            GetParent = {
              gameObject = "owner",
              storeResult = "{DriveTrigger}",
            },
          }, {
            GetParent = {
              gameObject = "{DriveTrigger}",
              storeResult = "{Car}",
            },
          }, {
            GetComponent = {
              gameObject = "{Car}",
              storeComponent = "{VehicleController}",
              everyFrame = false,
            },
          }, {
            SetProperties = {
              on = "{VehicleController}",
              ["input.ShiftUp"] = true,
            },
          } },
          transitions = {
            FINISHED = "shift",
          },
        },
        DOWN = {
          actions = { {
            GetParent = {
              gameObject = "owner",
              storeResult = "{DriveTrigger}",
            },
          }, {
            GetParent = {
              gameObject = "{DriveTrigger}",
              storeResult = "{Car}",
            },
          }, {
            GetComponent = {
              gameObject = "{Car}",
              storeComponent = "{VehicleController}",
              everyFrame = false,
            },
          }, {
            SetProperties = {
              on = "{VehicleController}",
              ["input.ShiftDown"] = true,
            },
          } },
          transitions = {
            FINISHED = "shift",
          },
        },
      },
    },
    INPUT_GearChange = {
      variables = {
        gameObject = {
          Car = null,
          DriveTrigger = null,
          GearChangeSound = null,
        },
        object = {
          VehicleController = {
            type = "NWH.VehiclePhysics2.VehicleController",
            value = null,
          },
          audio = {
            type = "UnityEngine.AudioClip",
            value = null,
          },
        },
      },
      start = "MAIN",
      states = {
        MAIN = {
          actions = { {
            GetButtonDown = {
              buttonName = "ShiftIntoR1",
              sendEvent = "R",
              storeResult = "{}",
            },
          }, {
            GetButtonDown = {
              buttonName = "ShiftIntoN",
              sendEvent = "N",
              storeResult = "{}",
            },
          }, {
            GetButtonDown = {
              buttonName = "ShiftInto1",
              sendEvent = "1",
              storeResult = "{}",
            },
          }, {
            GetButtonDown = {
              buttonName = "ShiftInto2",
              sendEvent = "2",
              storeResult = "{}",
            },
          }, {
            GetButtonDown = {
              buttonName = "ShiftInto3",
              sendEvent = "3",
              storeResult = "{}",
            },
          }, {
            GetButtonDown = {
              buttonName = "ShiftInto4",
              sendEvent = "4",
              storeResult = "{}",
            },
          }, {
            GetButtonDown = {
              buttonName = "ShiftInto5",
              sendEvent = "5",
              storeResult = "{}",
            },
          } },
          transitions = {
            R = "R",
            N = "N",
            ["1"] = "1",
            ["2"] = "2",
            ["3"] = "3",
            ["4"] = "4",
            ["5"] = "5",
          },
        },
        R = {
          actions = { {
            GetParent = {
              gameObject = "owner",
              storeResult = "{DriveTrigger}",
            },
          }, {
            GetParent = {
              gameObject = "{DriveTrigger}",
              storeResult = "{Car}",
            },
          }, {
            GetComponent = {
              gameObject = "{Car}",
              storeComponent = "{VehicleController}",
              everyFrame = false,
            },
          }, {
            SetProperties = {
              on = "{VehicleController}",
              ["input.ShiftInto"] = -1,
            },
          } },
          transitions = {
            FINISHED = "MAIN",
          },
        },
        N = {
          actions = { {
            GetParent = {
              gameObject = "owner",
              storeResult = "{DriveTrigger}",
            },
          }, {
            GetParent = {
              gameObject = "{DriveTrigger}",
              storeResult = "{Car}",
            },
          }, {
            GetComponent = {
              gameObject = "{Car}",
              storeComponent = "{VehicleController}",
              everyFrame = false,
            },
          }, {
            SetProperties = {
              on = "{VehicleController}",
              ["input.ShiftInto"] = 0,
            },
          } },
          transitions = {
            FINISHED = "MAIN",
          },
        },
        ["1"] = {
          actions = { {
            GetParent = {
              gameObject = "owner",
              storeResult = "{DriveTrigger}",
            },
          }, {
            GetParent = {
              gameObject = "{DriveTrigger}",
              storeResult = "{Car}",
            },
          }, {
            GetComponent = {
              gameObject = "{Car}",
              storeComponent = "{VehicleController}",
              everyFrame = false,
            },
          }, {
            SetProperties = {
              on = "{VehicleController}",
              ["input.ShiftInto"] = 1,
            },
          } },
          transitions = {
            FINISHED = "MAIN",
          },
        },
        ["2"] = {
          actions = { {
            GetParent = {
              gameObject = "owner",
              storeResult = "{DriveTrigger}",
            },
          }, {
            GetParent = {
              gameObject = "{DriveTrigger}",
              storeResult = "{Car}",
            },
          }, {
            GetComponent = {
              gameObject = "{Car}",
              storeComponent = "{VehicleController}",
              everyFrame = false,
            },
          }, {
            SetProperties = {
              on = "{VehicleController}",
              ["input.ShiftInto"] = 2,
            },
          } },
          transitions = {
            FINISHED = "MAIN",
          },
        },
        ["3"] = {
          actions = { {
            GetParent = {
              gameObject = "owner",
              storeResult = "{DriveTrigger}",
            },
          }, {
            GetParent = {
              gameObject = "{DriveTrigger}",
              storeResult = "{Car}",
            },
          }, {
            GetComponent = {
              gameObject = "{Car}",
              storeComponent = "{VehicleController}",
              everyFrame = false,
            },
          }, {
            SetProperties = {
              on = "{VehicleController}",
              ["input.ShiftInto"] = 3,
            },
          } },
          transitions = {
            FINISHED = "MAIN",
          },
        },
        ["4"] = {
          actions = { {
            GetParent = {
              gameObject = "owner",
              storeResult = "{DriveTrigger}",
            },
          }, {
            GetParent = {
              gameObject = "{DriveTrigger}",
              storeResult = "{Car}",
            },
          }, {
            GetComponent = {
              gameObject = "{Car}",
              storeComponent = "{VehicleController}",
              everyFrame = false,
            },
          }, {
            SetProperties = {
              on = "{VehicleController}",
              ["input.ShiftInto"] = 4,
            },
          } },
          transitions = {
            FINISHED = "MAIN",
          },
        },
        ["5"] = {
          actions = { {
            GetParent = {
              gameObject = "owner",
              storeResult = "{DriveTrigger}",
            },
          }, {
            GetParent = {
              gameObject = "{DriveTrigger}",
              storeResult = "{Car}",
            },
          }, {
            GetComponent = {
              gameObject = "{Car}",
              storeComponent = "{VehicleController}",
              everyFrame = false,
            },
          }, {
            SetProperties = {
              on = "{VehicleController}",
              ["input.ShiftInto"] = 5,
            },
          } },
          transitions = {
            FINISHED = "MAIN",
          },
        },
      },
    },
    INPUT_HShifter = {
      variables = {
        gameObject = {
          Car = null,
          DriveTrigger = null,
          GearChangeSound = null,
        },
        object = {
          VehicleController = {
            type = "NWH.VehiclePhysics2.VehicleController",
            value = null,
          },
          audio = {
            type = "UnityEngine.AudioClip",
            value = null,
          },
        },
      },
      start = "MAIN",
      states = {
        MAIN = {
          actions = { {
            GetParent = {
              gameObject = "owner",
              storeResult = "{DriveTrigger}",
            },
          }, {
            GetParent = {
              gameObject = "{DriveTrigger}",
              storeResult = "{Car}",
            },
          }, {
            GetComponent = {
              gameObject = "{Car}",
              storeComponent = "{VehicleController}",
              everyFrame = false,
            },
          }, {
            SetProperties = {
              on = "{VehicleController}",
              ["input.ShiftInto"] = 0,
            },
          }, {
            GetButtonDown = {
              buttonName = "ShiftIntoR1",
              sendEvent = "R",
              storeResult = "{}",
            },
          }, {
            GetButtonDown = {
              buttonName = "ShiftInto1",
              sendEvent = "1",
              storeResult = "{}",
            },
          }, {
            GetButtonDown = {
              buttonName = "ShiftInto2",
              sendEvent = "2",
              storeResult = "{}",
            },
          }, {
            GetButtonDown = {
              buttonName = "ShiftInto3",
              sendEvent = "3",
              storeResult = "{}",
            },
          }, {
            GetButtonDown = {
              buttonName = "ShiftInto4",
              sendEvent = "4",
              storeResult = "{}",
            },
          }, {
            GetButtonDown = {
              buttonName = "ShiftInto5",
              sendEvent = "5",
              storeResult = "{}",
            },
          } },
          transitions = {
            R = "R",
            ["1"] = "1",
            ["2"] = "2",
            ["3"] = "3",
            ["4"] = "4",
            ["5"] = "5",
          },
        },
        R = {
          actions = { {
            GetParent = {
              gameObject = "owner",
              storeResult = "{DriveTrigger}",
            },
          }, {
            GetParent = {
              gameObject = "{DriveTrigger}",
              storeResult = "{Car}",
            },
          }, {
            GetComponent = {
              gameObject = "{Car}",
              storeComponent = "{VehicleController}",
              everyFrame = false,
            },
          }, {
            SetProperties = {
              on = "{VehicleController}",
              ["input.ShiftInto"] = -1,
            },
          }, {
            GetButtonUp = {
              buttonName = "ShiftIntoR1",
              sendEvent = "FINISHED",
              storeResult = "{}",
            },
          } },
          transitions = {
            FINISHED = "MAIN",
          },
        },
        ["1"] = {
          actions = { {
            GetParent = {
              gameObject = "owner",
              storeResult = "{DriveTrigger}",
            },
          }, {
            GetParent = {
              gameObject = "{DriveTrigger}",
              storeResult = "{Car}",
            },
          }, {
            GetComponent = {
              gameObject = "{Car}",
              storeComponent = "{VehicleController}",
              everyFrame = false,
            },
          }, {
            SetProperties = {
              on = "{VehicleController}",
              ["input.ShiftInto"] = 1,
            },
          }, {
            GetButtonUp = {
              buttonName = "ShiftInto1",
              sendEvent = "FINISHED",
              storeResult = "{}",
            },
          } },
          transitions = {
            FINISHED = "MAIN",
          },
        },
        ["2"] = {
          actions = { {
            GetParent = {
              gameObject = "owner",
              storeResult = "{DriveTrigger}",
            },
          }, {
            GetParent = {
              gameObject = "{DriveTrigger}",
              storeResult = "{Car}",
            },
          }, {
            GetComponent = {
              gameObject = "{Car}",
              storeComponent = "{VehicleController}",
              everyFrame = false,
            },
          }, {
            SetProperties = {
              on = "{VehicleController}",
              ["input.ShiftInto"] = 2,
            },
          }, {
            GetButtonUp = {
              buttonName = "ShiftInto2",
              sendEvent = "FINISHED",
              storeResult = "{}",
            },
          } },
          transitions = {
            FINISHED = "MAIN",
          },
        },
        ["3"] = {
          actions = { {
            GetParent = {
              gameObject = "owner",
              storeResult = "{DriveTrigger}",
            },
          }, {
            GetParent = {
              gameObject = "{DriveTrigger}",
              storeResult = "{Car}",
            },
          }, {
            GetComponent = {
              gameObject = "{Car}",
              storeComponent = "{VehicleController}",
              everyFrame = false,
            },
          }, {
            SetProperties = {
              on = "{VehicleController}",
              ["input.ShiftInto"] = 3,
            },
          }, {
            GetButtonUp = {
              buttonName = "ShiftInto3",
              sendEvent = "FINISHED",
              storeResult = "{}",
            },
          } },
          transitions = {
            FINISHED = "MAIN",
          },
        },
        ["4"] = {
          actions = { {
            GetParent = {
              gameObject = "owner",
              storeResult = "{DriveTrigger}",
            },
          }, {
            GetParent = {
              gameObject = "{DriveTrigger}",
              storeResult = "{Car}",
            },
          }, {
            GetComponent = {
              gameObject = "{Car}",
              storeComponent = "{VehicleController}",
              everyFrame = false,
            },
          }, {
            SetProperties = {
              on = "{VehicleController}",
              ["input.ShiftInto"] = 4,
            },
          }, {
            GetButtonUp = {
              buttonName = "ShiftInto4",
              sendEvent = "FINISHED",
              storeResult = "{}",
            },
          } },
          transitions = {
            FINISHED = "MAIN",
          },
        },
        ["5"] = {
          actions = { {
            GetParent = {
              gameObject = "owner",
              storeResult = "{DriveTrigger}",
            },
          }, {
            GetParent = {
              gameObject = "{DriveTrigger}",
              storeResult = "{Car}",
            },
          }, {
            GetComponent = {
              gameObject = "{Car}",
              storeComponent = "{VehicleController}",
              everyFrame = false,
            },
          }, {
            SetProperties = {
              on = "{VehicleController}",
              ["input.ShiftInto"] = 5,
            },
          }, {
            GetButtonUp = {
              buttonName = "ShiftInto5",
              sendEvent = "FINISHED",
              storeResult = "{}",
            },
          } },
          transitions = {
            FINISHED = "MAIN",
          },
        },
      },
    },
    INPUT_MouseSteering = {
      variables = {
        float = {
          Steering = 0,
        },
        gameObject = {
          Car = null,
          DriveTrigger = null,
        },
        object = {
          VehicleController = {
            type = "NWH.VehiclePhysics2.VehicleController",
            value = null,
          },
        },
      },
      start = "input",
      states = {
        input = {
          actions = { {
            GetMouseX = {
              storeResult = "{Steering}",
              normalize = true,
              everyFrame = true,
            },
          }, {
            FloatRemap = {
              theFloat = "{Steering}",
              baseStart = 0,
              baseEnd = 1,
              targetStart = -1,
              targetEnd = 1,
              storeResult = "{Steering}",
              everyFrame = true,
            },
          }, {
            GetParent = {
              gameObject = "owner",
              storeResult = "{DriveTrigger}",
            },
          }, {
            GetParent = {
              gameObject = "{DriveTrigger}",
              storeResult = "{Car}",
            },
          }, {
            GetComponent = {
              gameObject = "{Car}",
              storeComponent = "{VehicleController}",
              everyFrame = false,
            },
          }, {
            SetProperties = {
              on = "{VehicleController}",
              ["input.Steering"] = "{Steering}",
              everyFrame = true,
            },
          } },
        },
      },
    },
    INPUT_NormalizedAxisInput = {
      variables = {
        float = {
          Brakes = 0,
          Clutch = 0,
          Throttle = 0,
          cruise = 0,
        },
        gameObject = {
          Car = null,
          DriveTrigger = null,
        },
        object = {
          VehicleController = {
            type = "NWH.VehiclePhysics2.VehicleController",
            value = null,
          },
        },
      },
      start = "input",
      states = {
        input = {
          actions = { {
            GetAxis = {
              axisName = "Throttle",
              multiplier = 1,
              invert = false,
              store = "{Throttle}",
              everyFrame = true,
            },
          }, {
            GetAxis = {
              axisName = "Brakes",
              multiplier = 1,
              invert = false,
              store = "{Brakes}",
              everyFrame = true,
            },
          }, {
            GetAxis = {
              axisName = "Clutch",
              multiplier = 1,
              invert = false,
              store = "{Clutch}",
              everyFrame = true,
            },
          }, {
            FloatRemap = {
              theFloat = "{Throttle}",
              baseStart = -1,
              baseEnd = 1,
              targetStart = 0,
              targetEnd = 1,
              storeResult = "{Throttle}",
              everyFrame = true,
            },
          }, {
            FloatRemap = {
              theFloat = "{Brakes}",
              baseStart = -1,
              baseEnd = 1,
              targetStart = 0,
              targetEnd = 1,
              storeResult = "{Brakes}",
              everyFrame = true,
            },
          }, {
            FloatRemap = {
              theFloat = "{Clutch}",
              baseStart = -1,
              baseEnd = 1,
              targetStart = 0,
              targetEnd = 1,
              storeResult = "{Clutch}",
              everyFrame = true,
            },
          }, {
            GetParent = {
              gameObject = "owner",
              storeResult = "{DriveTrigger}",
            },
          }, {
            GetParent = {
              gameObject = "{DriveTrigger}",
              storeResult = "{Car}",
            },
          }, {
            GetComponent = {
              gameObject = "{Car}",
              storeComponent = "{VehicleController}",
              everyFrame = false,
            },
          }, {
            GetFsmFloat = {
              gameObject = "owner",
              fsmName = "INPUT_CruiseControl",
              variableName = "cruise",
              storeValue = "{cruise}",
              everyFrame = true,
            },
          }, {
            FloatOperator = {
              float1 = "{Throttle}",
              float2 = "{cruise}",
              operation = "Max",
              storeResult = "{Throttle}",
              everyFrame = true,
            },
          }, {
            SetProperties = {
              on = "{VehicleController}",
              ["input.Throttle"] = "{Throttle}",
              everyFrame = true,
            },
          }, {
            SetProperties = {
              on = "{VehicleController}",
              ["input.Brakes"] = "{Brakes}",
              everyFrame = true,
            },
          }, {
            SetProperties = {
              on = "{VehicleController}",
              ["input.Clutch"] = "{Clutch}",
              everyFrame = true,
            },
          } },
        },
      },
    },
    INPUT_Horn = {
      globalEvents = { "Activate", "Deactivate", "useDoor" },
      start = "off",
      states = {
        on = {
          actions = { {
            GetButtonUp = {
              buttonName = "Horn",
              sendEvent = "next",
              storeResult = "{}",
            },
          }, {
            SetAudioClip = {
              gameObject = o.horn,
              audioClip = "asset:AudioClip/Horn",
            },
          }, {
            SetAudioLoop = {
              gameObject = o.horn,
              loop = true,
            },
          }, {
            AudioPlay = {
              gameObject = o.horn,
              volume = 1,
              oneShotClip = "{}",
              WaitForEndOfClip = false,
              finishedEvent = null,
            },
          } },
          transitions = {
            next = "off",
          },
        },
        off = {
          actions = { {
            GetButtonDown = {
              buttonName = "Horn",
              sendEvent = "next",
              storeResult = "{}",
            },
          }, {
            AudioStop = {
              gameObject = o.horn,
              fadeTime = 0,
            },
          } },
          transitions = {
            next = "on",
          },
        },
      },
    },
    Bool_AutomaticGearbox = {
      start = "Automatic",
      states = {
        Automatic = {
          actions = { {
            BoolTest = {
              boolVariable = "{automatic_gearbox}",
              isTrue = null,
              isFalse = "next",
              everyFrame = true,
            },
          }, {
            SetProperties = {
              on = "root:VehicleController",
              ["powertrain.transmission.transmissionType"] = "Automatic",
            },
          }, {
            SetProperties = {
              on = "root:VehicleController",
              ["input.swapInputInReverse"] = true,
            },
          } },
          transitions = {
            next = "Manual",
          },
        },
        Manual = {
          actions = { {
            BoolTest = {
              boolVariable = "{automatic_gearbox}",
              isTrue = "back",
              isFalse = null,
              everyFrame = true,
            },
          }, {
            SetProperties = {
              on = "root:VehicleController",
              ["powertrain.transmission.transmissionType"] = "Manual",
            },
          }, {
            SetProperties = {
              on = "root:VehicleController",
              ["input.swapInputInReverse"] = false,
            },
          } },
          transitions = {
            back = "Automatic",
          },
        },
      },
    },
    INPUT_CruiseControl = {
      variables = {
        gameObject = {
          Car = null,
          DriveTrigger = null,
        },
        object = {
          VehicleController = {
            type = "NWH.VehiclePhysics2.VehicleController",
            value = null,
          },
        },
        float = {
          cruise = 0,
        },
      },
      start = "off",
      states = {
        off = {
          actions = { {
            GetButtonDown = {
              buttonName = "Cruise Control",
              sendEvent = "next",
              storeResult = "{}",
            },
          }, {
            GetParent = {
              gameObject = "owner",
              storeResult = "{DriveTrigger}",
            },
          }, {
            GetParent = {
              gameObject = "{DriveTrigger}",
              storeResult = "{Car}",
            },
          }, {
            GetComponent = {
              gameObject = "{Car}",
              storeComponent = "{VehicleController}",
              everyFrame = false,
            },
          }, {
            SetFloatValue = {
              floatVariable = "{cruise}",
              floatValue = 0,
            },
          } },
          transitions = {
            next = "on",
          },
        },
        on = {
          actions = { {
            GetButtonDown = {
              buttonName = "Cruise Control",
              sendEvent = "back",
              storeResult = "{}",
            },
          }, {
            GetParent = {
              gameObject = "owner",
              storeResult = "{DriveTrigger}",
            },
          }, {
            GetParent = {
              gameObject = "{DriveTrigger}",
              storeResult = "{Car}",
            },
          }, {
            GetComponent = {
              gameObject = "{Car}",
              storeComponent = "{VehicleController}",
              everyFrame = false,
            },
          }, {
            SetFloatValue = {
              floatVariable = "{cruise}",
              floatValue = 0.8,
            },
          } },
          transitions = {
            back = "off",
          },
        },
      },
    },
    Bool_HShifter = {
      start = "OFF",
      states = {
        OFF = {
          actions = { {
            BoolTest = {
              boolVariable = "{hshifter}",
              isTrue = "next",
              isFalse = null,
              everyFrame = true,
            },
          }, {
            EnableFSM = {
              gameObject = "owner",
              fsmName = "INPUT_HShifter",
              enable = false,
              resetOnExit = false,
            },
          }, {
            EnableFSM = {
              gameObject = "owner",
              fsmName = "INPUT_GearChange",
              enable = true,
              resetOnExit = false,
            },
          } },
          transitions = {
            next = "ON",
          },
        },
        ON = {
          actions = { {
            BoolTest = {
              boolVariable = "{hshifter}",
              isTrue = null,
              isFalse = "back",
              everyFrame = true,
            },
          }, {
            EnableFSM = {
              gameObject = "owner",
              fsmName = "INPUT_HShifter",
              enable = true,
              resetOnExit = false,
            },
          }, {
            EnableFSM = {
              gameObject = "owner",
              fsmName = "INPUT_GearChange",
              enable = false,
              resetOnExit = false,
            },
          } },
          transitions = {
            back = "OFF",
          },
        },
      },
    },
    Bool_SmoothInput = {
      start = "OFF",
      states = {
        OFF = {
          actions = { {
            BoolTest = {
              boolVariable = "{smoothinput}",
              isTrue = "next",
              isFalse = null,
              everyFrame = true,
            },
          }, {
            SetProperties = {
              on = "root:VehicleController",
              ["steering.useRawInput"] = true,
            },
          } },
          transitions = {
            next = "ON",
          },
        },
        ON = {
          actions = { {
            BoolTest = {
              boolVariable = "{smoothinput}",
              isTrue = null,
              isFalse = "back",
              everyFrame = true,
            },
          }, {
            SetProperties = {
              on = "root:VehicleController",
              ["steering.useRawInput"] = false,
            },
          } },
          transitions = {
            back = "OFF",
          },
        },
      },
    },
    Bool_NormalizeInput = {
      start = "OFF",
      states = {
        OFF = {
          actions = { {
            BoolTest = {
              boolVariable = "{normalizeinput}",
              isTrue = "next",
              isFalse = null,
              everyFrame = true,
            },
          }, {
            EnableFSM = {
              gameObject = "owner",
              fsmName = "INPUT_NormalizedAxisInput",
              enable = false,
              resetOnExit = false,
            },
          }, {
            EnableFSM = {
              gameObject = "owner",
              fsmName = "INPUT_AxisInput",
              enable = true,
              resetOnExit = false,
            },
          } },
          transitions = {
            next = "ON",
          },
        },
        ON = {
          actions = { {
            BoolTest = {
              boolVariable = "{normalizeinput}",
              isTrue = null,
              isFalse = "back",
              everyFrame = true,
            },
          }, {
            EnableFSM = {
              gameObject = "owner",
              fsmName = "INPUT_NormalizedAxisInput",
              enable = true,
              resetOnExit = false,
            },
          }, {
            EnableFSM = {
              gameObject = "owner",
              fsmName = "INPUT_AxisInput",
              enable = false,
              resetOnExit = false,
            },
          } },
          transitions = {
            back = "OFF",
          },
        },
      },
    },
    Bool_SteeringSpeed = {
      start = "wait",
      states = {
        SteeringSpeed = {
          actions = { {
            SetProperties = {
              on = "root:VehicleController",
              ["steering.degreesPerSecondLimit"] = "{steeringspeed}",
            },
          } },
          transitions = {
            FINISHED = "wait",
          },
        },
        wait = {
          actions = { {
            Wait = {
              time = 2,
              finishEvent = "next",
              realTime = true,
            },
          } },
          transitions = {
            next = "SteeringSpeed",
          },
        },
      },
    },
    Bool_MouseSteering = {
      variables = {
        gameObject = {
          MouseSteeringUI = null,
        },
      },
      globalEvents = { "MouseSteeringLook", "Deactivate" },
      start = "OFF",
      states = {
        OFF = {
          actions = { {
            GetButtonDown = {
              buttonName = "Mouse Steering",
              sendEvent = "next",
              storeResult = "{}",
            },
          }, {
            FindGameObject = {
              objectName = "MouseSteeringUI",
              withTag = "Untagged",
              store = "{MouseSteeringUI}",
            },
          }, {
            ActivateGameObject = {
              gameObject = "{MouseSteeringUI}",
              activate = false,
              recursive = false,
              resetOnExit = false,
              everyFrame = false,
            },
          }, {
            SendEvent = {
              eventTarget = "{Player}/Look",
              sendEvent = "Deactivate",
              delay = 0,
              everyFrame = false,
            },
          }, {
            EnableFSM = {
              gameObject = "owner",
              fsmName = "INPUT_AxisSteering",
              enable = true,
              resetOnExit = false,
            },
          }, {
            EnableFSM = {
              gameObject = "owner",
              fsmName = "INPUT_MouseSteering",
              enable = false,
              resetOnExit = false,
            },
          } },
          transitions = {
            next = "ON",
          },
        },
        ON = {
          actions = { {
            GetButtonDown = {
              buttonName = "Mouse Steering",
              sendEvent = "back",
              storeResult = "{}",
            },
          }, {
            FindGameObject = {
              objectName = "MouseSteeringUI",
              withTag = "Untagged",
              store = "{MouseSteeringUI}",
            },
          }, {
            ActivateGameObject = {
              gameObject = "{MouseSteeringUI}",
              activate = true,
              recursive = false,
              resetOnExit = false,
              everyFrame = false,
            },
          }, {
            SendEvent = {
              eventTarget = "{Player}/Look",
              sendEvent = "MouseSteeringLook",
              delay = 0,
              everyFrame = false,
            },
          }, {
            EnableFSM = {
              gameObject = "owner",
              fsmName = "INPUT_AxisSteering",
              enable = false,
              resetOnExit = false,
            },
          }, {
            EnableFSM = {
              gameObject = "owner",
              fsmName = "INPUT_MouseSteering",
              enable = true,
              resetOnExit = false,
            },
          } },
          transitions = {
            back = "OFF",
          },
        },
      },
    },
    Bool_AutoClutch = {
      start = "OFF",
      states = {
        OFF = {
          actions = { {
            BoolTest = {
              boolVariable = "{autoclutch}",
              isTrue = "next",
              isFalse = null,
              everyFrame = true,
            },
          }, {
            SetProperties = {
              on = "root:VehicleController",
              ["powertrain.clutch.controlType"] = "UserInput",
            },
          } },
          transitions = {
            next = "ON",
          },
        },
        ON = {
          actions = { {
            BoolTest = {
              boolVariable = "{autoclutch}",
              isTrue = null,
              isFalse = "back",
              everyFrame = true,
            },
          }, {
            SetProperties = {
              on = "root:VehicleController",
              ["powertrain.clutch.controlType"] = "Automatic",
            },
          } },
          transitions = {
            back = "OFF",
          },
        },
      },
    },
  }
end
