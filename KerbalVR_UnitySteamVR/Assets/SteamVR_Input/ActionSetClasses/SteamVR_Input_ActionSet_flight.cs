//------------------------------------------------------------------------------
// <auto-generated>
//     This code was generated by a tool.
//     Runtime Version:4.0.30319.42000
//
//     Changes to this file may cause incorrect behavior and will be lost if
//     the code is regenerated.
// </auto-generated>
//------------------------------------------------------------------------------

namespace Valve.VR
{
    using System;
    using UnityEngine;
    
    
    public class SteamVR_Input_ActionSet_flight : Valve.VR.SteamVR_ActionSet
    {
        
        public virtual SteamVR_Action_Vector2 FlightStick
        {
            get
            {
                return SteamVR_Actions.flight_FlightStick;
            }
        }
        
        public virtual SteamVR_Action_Vector2 YawStick
        {
            get
            {
                return SteamVR_Actions.flight_YawStick;
            }
        }
        
        public virtual SteamVR_Action_Vector2 ThrottleStick
        {
            get
            {
                return SteamVR_Actions.flight_ThrottleStick;
            }
        }
    }
}
