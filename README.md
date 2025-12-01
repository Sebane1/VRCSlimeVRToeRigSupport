Adds experimental toe rig support compatible with VRChat avatars and specific versions of SlimeVR server.

**Instructions:**

- Import the unity package,
- Go to Tools -> Toe Rig -> Add Toe Tracking Compatibility
- Wait for a window to appear.
- Find your avatars expression parameters, drag the file in.
- Find your avatars animator controller, drag the file in.
- Tweak the numbered values in degrees as you desire, or keep them as is.
  - Curl Min is toes bending downwards.
  - Curl Max is toes bending upwards for tippy toes.
  - Splay values control how much toes can go sideways per toe
 - After the values are how you want find and assign the toe bones on your avatar rig. 
   - Use any many toes as your rig supports, Minimum 1 toe bone per foot, or as many as 5 toe bones per foot. More toe bones allows more flexibility.
- Click Generate Toe Support
- Your avatar should now have toe support with compatible versions of SlimeVR

You may wish to use an additional plugin called [OSCSmooth](https://github.com/regzo2/OSCmooth) to make sure the toes look smooth to other people over the network. If you are using OSC Smooth, click the "Uses OSC Smooth" checkbox before hitting generate.
You will have to use the OSCSmooth plugin AFTER running the initial generation.
