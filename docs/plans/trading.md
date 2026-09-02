We are going to develop a financial trading capability.

Philosophy, thoughts, guiding principles, vision:
- Evaluate lots of trading strategies, and lots of parameters for those strategies
- Historical trading data is difficult to source but current prices are relatively easy. We need to build our own financial database to track markets
- Trading stratgies will be presented in various formats - some may be published online, some may be my ideas, some may be provided as APIs or code or something
- I want to run long-term simulations of trading strategies to gain some degree of confidence in them before going live
- I want a control panel/admin interaction that shows all strategies as a top level interaction, with controls on how to run variations/parameters of those strategies. I want to gather results and metrics up in one place so it is easy for me to see at a glance what strategy has done well for a day or is doing the best at any particular time
- We will not be using real money for a while so security is not a concern
- i generally feel like the trading app should be its own silo because it might make sense to carve out into its own effort in the future. but it could be helpful to piggyback on existing projects too - we'll probably need an api/control plane, job running and aerie web api project has that already. i guess it's no big deal for you to set it up twice though. we will need to run compute jobs frequently and at various
    - ultimately with productization of aerie, i think the trading source should be its own silo but i don't know the best method of telling aerie infra what topolgy the trading app needs
    - maybe it should be its very own repo from the start? it's pretty orthogonal in purpose to what aerie does. it just plugs into the architecture and runs as a supported service with an SLA
        - pontificating a little now that my mind is here - i think the heart of `aerie-the-product` is a push-button home cloud setup/deployment that provides desired home services meeting SLAs (primarily for uptime/availability and data backup/recoverability). i want to open source or sell the groundwork for setting up a home cluster, and sideload my personal stuff into my own home deployment. and allow other folks to customize their own as well.
- we will need to run little compute jobs throughout the day,
- Not HFT - We are technically sophisticated compared to the norm, but not compared to big financial actors. We are not racing for tiny temporal arbitrage. We're looking for strategic algorithms that are not leveraging such nichey areas
- I won't be hooking money up to this for some time, so security, encryption, etc are not really concerns yet. We will do due diligence in time when we need to hook up to the financial system, but that is for the future if we find any promising strategies
- I would generally expect to have to develop and deploy code to introduce a whole new strategy. We can probably simplify our ontology if we make that assumption now, which is fine.

ask me clarifying questions and give me a simple pitch for an architectural layout that will give us the capabilities we need. once we arrive at consensus on it, i will want you to write a phased out implementation plan with discrete steps pre-thought, back into this document.
