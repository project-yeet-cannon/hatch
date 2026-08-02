I live in a house which is only about 40 years hold but was built for a climate that has changed since its building. It was seemingly built with little to no heating/cooling and all was added afterwards.

We have 11 different baseboard radiator thermostats controlling electric radiators in different rooms. There is a forced-air air conditioner connected to some of the rooms. Our home has a non-standard layout with large open areas and lots of room adjacencies.

I have a smart home system setup using HomeAssistant and the distributed system in this repository. Relevant devices hooked up to the system are:

- 6 hygrometers, read temperature/humidity from known locations
- 10 smart electric baseboard radiator thermostats, provide temperature/humidity; control mode and set temp
- 1 smart AC/blower thermostate, provide temperature/humidity; control fan, mode, and temp
- 2 standing fans controlled by smart switches whos on/off are readable/controllable. These provide air flow around the house where the existing ventilation is lacking

My motivating purpose for writing Aerie is to create a "fly-by-wire" climate brain for our home. With my family as users of the system, I want to be able to provide input to devices around the home to make it cooler or warmer. I want the climate brain to perform the most economical and effective change in the home device topography to accomplish the change. I want it to learn over time, self-perform science to figure out how various weather conditions and home control parameters interplay to create climate conditions throughout the home. I want it to give me suggestions and have me change things (e.g. move standing fans around, add fans, etc) to do hard science and figure out how to take climate control of my home. I want to do engineering shit like 3d model the house and run computational fluid dynamics (CFD) on air flows and deterministically figure out how to improve things. Over time I want to get my power bills down and my home naturally comfortable at all times without thinking about it.

I want to build in MVP which is as minimal as possible. We can make assumptions - for instance, it is August and we do not need to worry about heating the house. No need to control the baseboard radiators, just the AC and fans.

