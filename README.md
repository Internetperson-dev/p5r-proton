# p5r-proton

See:
- https://github.com/ValveSoftware/Proton/issues/8687#:~:text=Though%2C%20reverting%20to%20Proton%207%2E0%2D6%20seems%20to%20make%20the%20game%20playable%20for%20me%2C%20unlike%20%40a%2Dwip0
- https://github.com/ValveSoftware/Proton/issues/8617#:~:text=The%20menu%20and%20game%20runs%20at%20%7E25%2D30%20FPS%20using%20Proton%2D8%2E0%2D5%20and%20Proton%20Experimental%2E%20I%20didn%27t%20try%20Proton%209%2E0%2D4%2E%20It%20runs%20at%20%7E60%20FPS%20using%20Proton%207%2E0%2D6%20using%20the%20same%20configuration

An attempt to try and solve an issue with Persona 5 Royal in versions past Proton 7 which had much higher CPU usage and a lower framerate.

Right now performance is about equal to Proton 7, but the CPU usage is still much higher. it may be due to changes in Wine's CPU code, or DXVK updates. 

I think the best way to solve it would be to have a Reloaded-II modification that profiles the game, and find what is different between Proton 7 and the latest version of Proton.

Even on Windows it seems the way Persona 5 Royal's PC port uses CPU is inefficient. 
