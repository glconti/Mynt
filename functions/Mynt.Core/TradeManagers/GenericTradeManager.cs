﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Table;
using Mynt.Core.Api;
using Mynt.Core.Enums;
using Mynt.Core.Interfaces;
using Mynt.Core.Managers;
using Mynt.Core.Models;

namespace Mynt.Core.TradeManagers
{
    public class GenericTradeManager
    {
        private readonly IExchangeApi _api;
        private readonly INotificationManager _notification;
        private readonly ITradingStrategy _strategy;
        private readonly Action<string> _log;
        private CloudTable _orderTable;
        private CloudTable _traderTable;
        private List<Trade> _activeTrades;
        private List<Trader> _currentTraders;
        private TableBatchOperation _orderBatch;
        private TableBatchOperation _traderBatch;

        public GenericTradeManager(IExchangeApi api, ITradingStrategy strat, INotificationManager notificationManager, Action<string> log)
        {
            _api = api;
            _strategy = strat;
            _log = log;
            _notification = notificationManager;
        }

        public async Task Initialize()
        {
            // First initialize a few things
            _orderTable = await ConnectionManager.GetTableConnection(Constants.OrderTableName, Constants.IsDryRunning);
            _traderTable = await ConnectionManager.GetTableConnection(Constants.TraderTableName, Constants.IsDryRunning);

            _activeTrades = _orderTable.CreateQuery<Trade>().Where(x => x.IsOpen).ToList();
            _currentTraders = _traderTable.CreateQuery<Trader>().ToList();

            // Create our trader records if they don't exist yet.
            if (_currentTraders.Count == 0) await CreateTradersIfNoneExist();

            _currentTraders = _traderTable.CreateQuery<Trader>().ToList();

            // Create a batch that we can use to update our table.
            _orderBatch = new TableBatchOperation();
            _traderBatch = new TableBatchOperation();
        }

        /// <summary>
        /// Checks if new trades can be started.
        /// </summary>
        /// <returns></returns>
        public async Task CheckStrategySignals()
        {
            // Initialize the things we'll be using throughout the process.
            await Initialize();

            // This means an order to buy has been open for an entire buy cycle.
            if (Constants.CancelUnboughtOrdersEachCycle)
                await CancelUnboughtOrders();

            // Check active trades against our strategy.
            // If the strategy tells you to sell, we create a sell.
            await CheckActiveTradesAgainstStrategy();

            // Check if there is room for more trades
            var freeTraders = _traderTable.CreateQuery<Trader>().Where(x => !x.IsBusy).ToList();

            // We have available traders to work for us!
            if (freeTraders.Count > 0)
            {
                // There's room for more.
                var trades = await FindBuyOpportunities();

                if (trades.Count > 0)
                {
                    // Depending on what we have more of we create trades.
                    var loopCount = freeTraders.Count >= trades.Count ? trades.Count : freeTraders.Count;

                    // Only create trades for our free traders
                    for (int i = 0; i < loopCount; i++)
                    {
                        await CreateNewTrade(freeTraders[i], trades[i]);
                    }
                }
                else
                {
                    _log("No trade opportunities found...");
                }
            }

            // Save any changes we may have made.
            if (_traderBatch.Count > 0) await _traderTable.ExecuteBatchAsync(_traderBatch);
            if (_orderBatch.Count > 0) await _orderTable.ExecuteBatchAsync(_orderBatch);
        }

        #region SETUP

        /// <summary>
        /// Creates trader objects that run in their own little bubble.
        /// </summary>
        /// <returns></returns>
        private async Task CreateTradersIfNoneExist()
        {
            var tableBatch = new TableBatchOperation();

            for (var i = 0; i < Constants.MaxNumberOfConcurrentTrades; i++)
            {
                var newTrader = new Trader()
                {
                    CurrentBalance = Constants.AmountOfBtcToInvestPerTrader,
                    IsBusy = false,
                    LastUpdated = DateTime.UtcNow,
                    StakeAmount = Constants.AmountOfBtcToInvestPerTrader,
                    RowKey = Guid.NewGuid().ToString().Replace("-", string.Empty),
                    PartitionKey = "TRADER"
                };

                tableBatch.Add(TableOperation.Insert(newTrader));
            }

            // Add our trader records
            if (tableBatch.Count > 0) await _traderTable.ExecuteBatchAsync(tableBatch);
        }

        #endregion

        #region STRATEGY RELATED

        /// <summary>
        /// Cancels any orders that have been buying for an entire cycle.
        /// </summary>
        /// <returns></returns>
        private async Task CancelUnboughtOrders()
        {
            // Only trigger if there are orders still buying.
            if (_activeTrades.Any(x => x.IsBuying))
            {
                // Loop our current buying trades if there are any.
                foreach (var trade in _activeTrades.Where(x => x.IsBuying))
                {
                    var exchangeOrder = await _api.GetOrder(trade.BuyOrderId, trade.Market);
                    
                    // if this order is PartiallyFilled, don't cancel
                    if (exchangeOrder?.Status == OrderStatus.PartiallyFilled)
                        continue;  // not yet completed so wait
                    
                    // Cancel our open buy order.
                    await _api.CancelOrder(trade.BuyOrderId, trade.Market);

                    // Update the buy order.
                    trade.IsBuying = false;
                    trade.OpenOrderId = null;
                    trade.IsOpen = false;
                    trade.SellType = SellType.Cancelled;
                    trade.CloseDate = DateTime.UtcNow;

                    // Update the order in our batch.
                    _orderBatch.Add(TableOperation.Replace(trade));

                    // Handle the trader that was dedicated to this order.
                    var currentTrader = _currentTraders.FirstOrDefault(x => x.RowKey == trade.TraderId);

                    if (currentTrader != null)
                    {
                        currentTrader.IsBusy = false;
                        currentTrader.LastUpdated = DateTime.UtcNow;

                        // Update the trader to indicate that we're not busy anymore.
                        await _traderTable.ExecuteAsync(TableOperation.Replace(currentTrader));
                    }

                    await SendNotification($"Cancelled {trade.Market} buy order.");
                }
            }
        }

        /// <summary>
        /// Creates a new trade in our system and opens a buy order.
        /// </summary>
        /// <param name="freeTrader"></param>
        /// <param name="trade"></param>
        /// <returns></returns>
        private async Task CreateNewTrade(Trader freeTrader, string trade)
        {
            // Get our Bitcoin balance from the exchange
            var currentBtcBalance = await _api.GetBalance("BTC");

            // Do we even have enough funds to invest?
            if (currentBtcBalance.Available < freeTrader.CurrentBalance)
                throw new Exception("Insufficient BTC funds to perform a trade.");

            var order = await CreateBuyOrder(freeTrader, trade);

            // We found a trade and have set it all up!
            if (order != null)
            {
                // Send a notification that we found something suitable
                _log($"New trade signal {order.Market}...");

                // Update the trader to busy
                freeTrader.LastUpdated = DateTime.UtcNow;
                freeTrader.IsBusy = true;
                _traderBatch.Add(TableOperation.Replace(freeTrader));

                // Create the trade record as well
                _orderBatch.Add(TableOperation.Insert(order));
            }
        }

        /// <summary>
        /// Checks our current running trades against the strategy.
        /// If the strategy tells us to sell we need to do so.
        /// </summary>
        /// <returns></returns>
        private async Task CheckActiveTradesAgainstStrategy()
        {
            // Check our active trades for a sell signal from the strategy
            foreach (var trade in _activeTrades.Where(x => (x.OpenOrderId == null || x.SellType == SellType.Immediate) && x.IsOpen))
            {
                var signal = await GetStrategySignal(trade.Market);

                // If the strategy is telling us to sell we need to do so.
                if (signal != null && signal.TradeAdvice == TradeAdvice.Sell)
                {
                    if ((trade.IsSelling && trade.SellType == SellType.Immediate))
                    {
                        // If an immediate order is placed it needs to be cancelled first.
                        await _api.CancelOrder(trade.OpenOrderId, trade.Market);
                    }

                    // Create a sell order for our strategy.
                    var ticker = await _api.GetTicker(trade.Market);
                    var orderId = await _api.Sell(trade.Market, trade.Quantity, ticker.Bid);

                    trade.CloseRate = ticker.Bid;
                    trade.OpenOrderId = orderId;
                    trade.SellOrderId = orderId;
                    trade.SellType = SellType.Strategy;
                    trade.IsSelling = true;

                    _orderBatch.Add(TableOperation.Replace(trade));

                    await SendNotification($"Sell order placed for {trade.Market} at {trade.CloseRate:0.00000000} (Strategy sell).");
                }
            }
        }

        /// <summary>
        /// Checks the implemented trading indicator(s),
        /// if one pair triggers the buy signal a new trade record gets created.
        /// </summary>
        /// <returns></returns>
        private async Task<List<string>> FindBuyOpportunities()
        {
            // Retrieve our current markets
            var markets = await _api.GetMarketSummaries();
            var pairs = new List<string>();

            // Check if there are markets matching our volume.
            markets = markets.Where(x =>
                (x.BaseVolume > Constants.MinimumAmountOfVolume ||
                Constants.AlwaysTradeList.Contains(x.CurrencyPair.BaseCurrency)) &&
                x.CurrencyPair.QuoteCurrency.ToUpper() == "BTC").ToList();

            // Remove existing trades from the list to check.
            foreach (var trade in _activeTrades)
                markets.RemoveAll(x => x.MarketName == trade.Market);

            // Remove items that are on our blacklist.
            foreach (var market in Constants.MarketBlackList)
                markets.RemoveAll(x => x.CurrencyPair.BaseCurrency == market);

            // Prioritize markets with high volume.
            foreach (var market in markets.Distinct().OrderByDescending(x => x.BaseVolume).ToList())
            {
                var signal = await GetStrategySignal(market.MarketName);

                // A match was made, buy that please!
                if (signal != null && signal.TradeAdvice == TradeAdvice.Buy)
                {
                    pairs.Add(market.MarketName);
                }
            }

            return pairs;
        }

        /// <summary>
        /// Creates a buy order on the exchange.
        /// </summary>
        /// <param name="freeTrader">The trader placing the order</param>
        /// <param name="pair">The pair we're buying</param>
        /// <returns></returns>
        private async Task<Trade> CreateBuyOrder(Trader freeTrader, string pair)
        {
            // Take the amount to invest per trader OR the current balance for this trader.
            var btcToSpend = freeTrader.CurrentBalance > Constants.AmountOfBtcToInvestPerTrader
                ? Constants.AmountOfBtcToInvestPerTrader
                : freeTrader.CurrentBalance;

            // The amount here is an indication and will probably not be precisely what you get.
            var ticker = await _api.GetTicker(pair);
            var openRate = GetTargetBid(ticker);
            var amount = btcToSpend / openRate;
            var amountYouGet = (btcToSpend * (1 - Constants.TransactionFeePercentage)) / openRate;

            // Get the order ID, this is the most important because we need this to check
            // up on our trade. We update the data below later when the final data is present.
            var orderId = await _api.Buy(pair, amount, openRate);

            await SendNotification($"Buying {pair} at {openRate:0.0000000 BTC} which was spotted at bid: {ticker.Bid:0.00000000}, " +
                                   $"ask: {ticker.Ask:0.00000000}, " +
                                   $"last: {ticker.Last:0.00000000}, " +
                                   $"({amountYouGet:0.0000} units).");

            return new Trade()
            {
                TraderId = freeTrader.RowKey,
                Market = pair,
                StakeAmount = btcToSpend,
                OpenRate = openRate,
                OpenDate = DateTime.UtcNow,
                Quantity = amountYouGet,
                OpenOrderId = orderId,
                BuyOrderId = orderId,
                IsOpen = true,
                IsBuying = true,
                StrategyUsed = _strategy.Name,
                PartitionKey = "TRADE",
                SellType = SellType.None,
                RowKey = $"MNT{(DateTime.MaxValue.Ticks - DateTime.UtcNow.Ticks):d19}"
            };
        }

        /// <summary>
        /// Calculates a buy signal based on several technical analysis indicators.
        /// </summary>
        /// <param name="market">The market we're going to check against.</param>
        /// <returns></returns>
        private async Task<ITradeAdvice> GetStrategySignal(string market)
        {
            try
            {
                _log($"Checking market {market}...");

                var minimumDate = _strategy.GetMinimumDateTime();
                var candleDate = _strategy.GetCurrentCandleDateTime();
                var candles = await _api.GetTickerHistory(market, minimumDate, _strategy.IdealPeriod);

                // We eliminate all candles that aren't needed for the dataset incl. the last one (if it's the current running candle).
                candles = candles.Where(x => x.Timestamp >= minimumDate && x.Timestamp < candleDate).ToList();

                // Not enough candles to perform what we need to do.
                if (candles.Count < _strategy.MinimumAmountOfCandles)
                    return new SimpleTradeAdvice(TradeAdvice.Hold);

                // Get the date for the last candle.
                var signalDate = candles[candles.Count - 1].Timestamp;

                // This is an outdated candle...
                if (signalDate < _strategy.GetSignalDate())
                    return null;

                // This calculates an advice for the next timestamp.
                var advice = _strategy.Forecast(candles);

                return advice;
            }
            catch (Exception)
            {
                // Couldn't get a buy signal for this market, no problem. Let's skip it.
                _log($"Couldn't get buy signal for {market}...");
                return null;
            }
        }

        /// <summary>
        /// Calculates bid target between current ask price and last price.
        /// </summary>
        /// <param name="tick"></param>
        /// <returns></returns>
        private double GetTargetBid(Ticker tick)
        {
            if (Constants.BuyInPriceStrategy == BuyInPriceStrategy.AskLastBalance)
            {
                // If the ask is below the last, we can get it on the cheap.
                if (tick.Ask < tick.Last) return tick.Ask;

                return tick.Ask + Constants.AskLastBalance * (tick.Last - tick.Ask);
            }
            else
            {
                return Math.Round(tick.Bid * (1 - Constants.BuyInPricePercentage), 8);
            }
        }

        #endregion

        #region UPDATE TRADES

        public async Task UpdateRunningTrades()
        {
            // Get our current trades.
            await Initialize();

            // First we update our open buy orders by checking if they're filled.
            await UpdateOpenBuyOrders();

            // Secondly we check if currently selling trades can be marked as sold if they're filled.
            await UpdateOpenSellOrders();

            // Third, our current trades need to be checked if one of these has hit its sell targets...
            await CheckForSellConditions();
        }

        /// <summary>
        /// Updates the buy orders by checking with the exchange what status they are currently.
        /// </summary>
        /// <returns></returns>
        private async Task UpdateOpenBuyOrders()
        {
            // There are trades that have an open order ID set & no sell order id set
            // that means its a buy trade that is waiting to get bought. See if we can update that first.
            _orderBatch = new TableBatchOperation();

            foreach (var trade in _activeTrades.Where(x => x.OpenOrderId != null && x.SellOrderId == null))
            {
                var exchangeOrder = await _api.GetOrder(trade.BuyOrderId, trade.Market);

                // if this order is filled, we can update our database.
                if (exchangeOrder?.Status == OrderStatus.Filled)
                {
                    trade.OpenOrderId = null;
                    trade.StakeAmount = exchangeOrder.OriginalQuantity * exchangeOrder.Price;
                    trade.Quantity = exchangeOrder.OriginalQuantity;
                    trade.OpenRate = exchangeOrder.Price;
                    trade.OpenDate = exchangeOrder.Time;
                    trade.IsBuying = false;

                    // If this is enabled we place a sell order as soon as our buy order got filled.
                    if (Constants.ImmediatelyPlaceSellOrder)
                    {
                        var sellPrice = Math.Round(trade.OpenRate * (1 + Constants.ImmediatelyPlaceSellOrderAtProfit), 8);
                        var orderId = await _api.Sell(trade.Market, trade.Quantity, sellPrice);

                        trade.CloseRate = sellPrice;
                        trade.OpenOrderId = orderId;
                        trade.SellOrderId = orderId;
                        trade.IsSelling = true;
                        trade.SellType = SellType.Immediate;
                    }

                    _orderBatch.Add(TableOperation.Replace(trade));

                    await SendNotification($"Buy order filled for {trade.Market} at {trade.OpenRate:0.00000000}.");
                }
            }

            if (_orderBatch.Count > 0) await _orderTable.ExecuteBatchAsync(_orderBatch);
        }

        /// <summary>
        /// Checks the current active trades if they need to be sold.
        /// </summary>
        /// <returns></returns>
        private async Task CheckForSellConditions()
        {
            // There are trades that have no open order ID set & are still open.
            // that means its a trade that is waiting to get sold. See if we can update that first.
            _orderBatch = new TableBatchOperation();

            // An open order currently not selling or being an immediate sell are checked for SL  etc.
            foreach (var trade in _activeTrades.Where(x => (x.OpenOrderId == null || x.SellType == SellType.Immediate) && x.IsOpen))
            {
                // These are trades that are not being bought or sold at the moment so these need to be checked for sell conditions.
                var ticker = await _api.GetTicker(trade.Market);
                var sellType = ShouldSell(trade, ticker.Bid, DateTime.UtcNow);

                if (sellType != SellType.None)
                {
                    if (trade.SellType == SellType.Immediate)
                    {
                        // Immediates need to be cancelled first.
                        await _api.CancelOrder(trade.SellOrderId, trade.Market);
                    }

                    var orderId = await _api.Sell(trade.Market, trade.Quantity, ticker.Bid);

                    trade.CloseRate = ticker.Bid;
                    trade.OpenOrderId = orderId;
                    trade.SellOrderId = orderId;
                    trade.SellType = sellType;
                    trade.IsSelling = true;

                    _orderBatch.Add(TableOperation.Replace(trade));

                    await SendNotification($"Going to sell {trade.Market} at {trade.CloseRate:0.00000000}.");
                }
                else if (sellType == SellType.TrailingStopLossUpdated)
                {
                    // Update the stop loss for this trade, which was set in ShouldSell.
                    _log($"Updated the trailing stop loss for {trade.Market} to {trade.StopLossRate.Value:0.00000000}");
                    _orderBatch.Add(TableOperation.Replace(trade));
                }
            }

            if (_orderBatch.Count > 0) await _orderTable.ExecuteBatchAsync(_orderBatch);
        }

        /// <summary>
        /// Based on earlier trade and current price and configuration, decides whether bot should sell.
        /// </summary>
        /// <param name="trade"></param>
        /// <param name="currentRateBid"></param>
        /// <param name="utcNow"></param>
        /// <returns>True if bot should sell at current rate.</returns>
        private SellType ShouldSell(Trade trade, double currentRateBid, DateTime utcNow)
        {
            var currentProfit = (currentRateBid - trade.OpenRate) / trade.OpenRate;

            _log($"Should sell {trade.Market}? Profit: {(currentProfit * 100):0.00}%...");

            // Let's not do a stoploss for now...
            if (currentProfit < Constants.StopLossPercentage)
            {
                _log($"Stop loss hit: {Constants.StopLossPercentage}%");
                return SellType.StopLoss;
            }

            // Check if time matches and current rate is above threshold
            foreach (var item in Constants.ReturnOnInvestment)
            {
                var timeDiff = (utcNow - trade.OpenDate).TotalSeconds / 60;

                if (timeDiff > item.Duration && currentProfit > item.Profit)
                {
                    _log($"Timer hit: {timeDiff} mins, profit {item.Profit:0.00}%");
                    return SellType.Timed;
                }
            }

            // Only run this when we're past our starting percentage for trailing stop.
            if (Constants.EnableTrailingStop)
            {
                // If the current rate is below our current stoploss percentage, close the trade.
                if (trade.StopLossRate.HasValue && currentRateBid < trade.StopLossRate.Value)
                    return SellType.TrailingStopLoss;

                // The new stop would be at a specific percentage above our starting point.
                var newStopRate = trade.OpenRate * (1 + (currentProfit - Constants.TrailingStopPercentage));

                // Only update the trailing stop when its above our starting percentage and higher than the previous one.
                if (currentProfit > Constants.TrailingStopStartingPercentage && (trade.StopLossRate < newStopRate || !trade.StopLossRate.HasValue))
                {
                    // The current profit percentage is high enough to create the trailing stop value.
                    trade.StopLossRate = newStopRate;

                    return SellType.TrailingStopLossUpdated;
                }

                return SellType.None;
            }

            return SellType.None;
        }

        /// <summary>
        /// Updates the sell orders by checking with the exchange what status they are currently.
        /// </summary>
        /// <returns></returns>
        private async Task UpdateOpenSellOrders()
        {
            // There are trades that have an open order ID set & sell order id set
            // that means its a sell trade that is waiting to get sold. See if we can update that first.
            _orderBatch = new TableBatchOperation();
            _traderBatch = new TableBatchOperation();

            foreach (var order in _activeTrades.Where(x => x.OpenOrderId != null && x.SellOrderId != null))
            {
                var exchangeOrder = await _api.GetOrder(order.SellOrderId, order.Market);

                // if this order is filled, we can update our database.
                if (exchangeOrder?.Status == OrderStatus.Filled)
                {
                    order.OpenOrderId = null;
                    order.IsOpen = false;
                    order.IsSelling = false;
                    order.CloseDate = exchangeOrder.Time;
                    order.CloseRate = exchangeOrder.Price;

                    order.CloseProfit = (exchangeOrder.Price * exchangeOrder.OriginalQuantity) - order.StakeAmount;
                    order.CloseProfitPercentage = ((exchangeOrder.Price * exchangeOrder.OriginalQuantity) - order.StakeAmount) / order.StakeAmount * 100;

                    // Retrieve the trader responsible for this trade
                    var trader = _currentTraders.FirstOrDefault(x => x.RowKey == order.TraderId);

                    if (trader != null)
                    {
                        trader.IsBusy = false;
                        trader.CurrentBalance += order.CloseProfit.Value;
                        trader.LastUpdated = DateTime.UtcNow;
                    }

                    _traderBatch.Add(TableOperation.Replace(trader));
                    _orderBatch.Add(TableOperation.Replace(order));

                    await SendNotification($"Sold {order.Market} at {order.CloseRate:0.00000000} for {order.CloseProfit:0.00000000} profit ({order.CloseProfitPercentage:0.00}%).");
                }
            }

            if (_traderBatch.Count > 0) await _traderTable.ExecuteBatchAsync(_traderBatch);
            if (_orderBatch.Count > 0) await _orderTable.ExecuteBatchAsync(_orderBatch);
        }

        #endregion

        private async Task SendNotification(string message)
        {
            if (_notification != null)
            {
                await _notification.SendNotification(message);
            }
        }
    }
}

