We are going to develop a financial trading capability.

Philosophy, thoughts, guiding principles, vision:
- Evaluate lots of trading strategies, and lots of parameters for those strategies
- Historical trading data is difficult to source but current prices are relatively easy. We need to build our own financial database to track markets
- Trading stratgies will be presented in various formats - some may be published online, some may be my ideas, some may be provided as APIs or code or something
- I want to run long-term simulations of trading strategies to gain some degree of confidence in them before going live
- I want a control panel/admin interaction that shows all strategies as a top level interaction, with controls on how to run variations/parameters of those strategies. I want to gather results and metrics up in one place so it is easy for me to see at a glance what strategy has done well for a day or is doing the best at any particular time
- We will not be using real money for a while so security is not a concern
- I want the trading app to be a standalone silo in the aerie repo. It may need to be carved out into its own repo at some point and I want to keep that easy. It should still be included in the aerie infrastructure though. We will need some sort of data store, any number of compute runs for strategy executions and market condition measurements, as well as either a web head for a dashboard or some cool voodo grafana config
- Not HFT - We are technically sophisticated compared to the norm, but not compared to big financial actors. We are not racing for tiny temporal arbitrage. We're looking for strategic algorithms that are not leveraging such nichey areas
- I won't be hooking money up to this for some time, so security, encryption, etc are not really concerns yet. We will do due diligence in time when we need to hook up to the financial system, but that is for the future if we find any promising strategies
- I would generally expect to have to develop and deploy code to introduce a whole new strategy. We can probably simplify our ontology if we make that assumption now, which is fine.
