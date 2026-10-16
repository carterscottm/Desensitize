# Desensitize

An admittedly vibe-coded patch to keep zoomed-in range rings on the tactical map from slowing down or crashing the game, particularly under Wine.

Aurora draws every active/passive sensor, fire control and weapons range rings as individual dashed ellipses.  Zoomed in far enough, the radius can reach millions of pixels.  GDI+ then has to build and dash a path that is mostly off screen for each ellipse, which is extremely slow and can exhaust memory.  This patch intercepts those draw calls and either skips rings that cannot be seen or draws only the visible part of the arc.

# Credits

Thank you Steve, not only for all the hard work and countless hours you've poured into the Aurora4X project, but for making it publicly available for others to enjoy as well!
