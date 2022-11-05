﻿using Google.Protobuf;
using Grpc.Core;
using Nethereum.ABI;
using Nethereum.Contracts;
using Nethereum.Contracts.Standards.ERC20.ContractDefinition;
using Nethereum.Signer;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RestSharp;
using Simple.Chain.Crypto;
using Simple.Chain.Protocol;
using System.Numerics;

namespace Simple.Chain.Tron
{
    /// <summary>
    /// 钱包相关
    /// </summary>
    public class TronWallet : IWallet
    {
        private readonly Wallet.WalletClient _wallet;
        public TronWallet()
        {
            Channel channel = new Channel("grpc.trongrid.io", 50051, ChannelCredentials.Insecure);
            _wallet = new Wallet.WalletClient(channel);
        }
        /// <summary>
        /// 默认主网
        /// </summary>
        private const string DEFAULT_MAINNET = "https://api.trongrid.io";


        private readonly string baseUrl;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="mainnet">主网</param>
        /// <param name="testnet">测试（建议自行部署）</param>
        /// <param name="proxy">代理地址</param>
        public TronWallet(string mainnet = DEFAULT_MAINNET)
        {
            baseUrl = mainnet;
        }

        /// <summary>
        /// 创建请求客户端
        /// </summary>
        /// <param name="requesturi"></param>
        /// <param name="method"></param>
        /// <returns></returns>
        private RestClient CreateClient(string requesturi, Method method, out RestRequest request)
        {
            RestClient client;
            client = new RestClient(baseUrl);
            request = new RestRequest(requesturi, method);
            return client;
        }

        public async Task<string> CreateAccountAsync(string privateKey, string address)
        {
            string owner_address = privateKey.ToAddress();

            byte[] ownerAddress = Base58Encoder.DecodeFromBase58Check(owner_address);
            byte[] acountAddress = Base58Encoder.DecodeFromBase58Check(address);

            AccountCreateContract contract = new()
            {
                AccountAddress = ByteString.CopyFrom(acountAddress),
                OwnerAddress = ByteString.CopyFrom(ownerAddress)
            };
            Transaction transaction = await _wallet.CreateAccountAsync(contract);

            SignTransaction(transaction, privateKey);

            var result = await _wallet.BroadcastTransactionAsync(transaction);

            if (!result.Result) throw new ChainException($"广播失败：{result.Message.ToStringUtf8()}", address: owner_address);

            return transaction.GetTxID();
        }


        public async Task<AccountInfo> GetAccountAsync(string address)
        {
            byte[] ownerAddress = Base58Encoder.DecodeFromBase58Check(address);
            Account request = new()
            {
                Address = ByteString.CopyFrom(ownerAddress),
            };
            var response = await _wallet.GetAccountAsync(request);
            return new AccountInfo
            {
                Address = address,
                Balance = response.Balance.ToSum(),
                Gas = 4.102M
            };
        }

        public async Task<AccountInfo> GetAccountAsync(string address, string contract_address)
        {
            byte[] ownerAddress = Base58Encoder.DecodeFromBase58Check(address);
            byte[] contractAddress = Base58Encoder.DecodeFromBase58Check(contract_address);
            ABIEncode abiEncode = new();
            string encode = "0x70a08231" + abiEncode.GetABIParamsEncoded(new BalanceOfFunction() { Owner = address.ToHexAddress(false) }).ToHex();

            TriggerSmartContract contract = new()
            {
                ContractAddress = ByteString.CopyFrom(contractAddress),
                OwnerAddress = ByteString.CopyFrom(ownerAddress),
                Data = ByteString.CopyFrom(encode.HexToByteArray()),
            };
            TransactionExtention transactionExtention = await _wallet.TriggerConstantContractAsync(contract);
            if (transactionExtention.ConstantResult.Count == 0) throw new ChainException("合约执行错误", address: address);
            var result = transactionExtention.ConstantResult[0];
            string hex = "0000000000000000000000000000000000000000000000000000000000000000" + result.ToByteArray().ToHex();
            TransferFunction balanceOf = new TransferFunction().DecodeInput(hex);
            return new AccountInfo
            {
                Address = address,
                Balance = balanceOf.Value.ToSum()
            };
        }

        /// <summary>
        /// 转账签名
        /// </summary>
        /// <param name="transaction"></param>
        /// <param name="privateKey"></param>
        private void SignTransaction(Transaction transaction, string privateKey)
        {
            var ecKey = new ECKey(privateKey.HexToByteArray(), true);
            var hash = transaction.RawData.ToByteArray().ToSHA256Hash();
            var sign = ecKey.Sign(hash).ToByteArray();
            transaction.Signature.Add(ByteString.CopyFrom(sign));
        }

        public async Task<string> TransferAsync(string privateKey, string to_address, decimal amount)
        {
            string owner_address = privateKey.ToAddress();
            //检查余额
            AccountInfo account = await this.GetAccountAsync(owner_address);
            if (account.Balance < amount) throw new ChainException($"余额不足：{account.Balance}", address: owner_address);
            byte[] fromAddress = Base58Encoder.DecodeFromBase58Check(owner_address);
            byte[] toAddress = Base58Encoder.DecodeFromBase58Check(to_address);
            TransferContract transfer = new()
            {
                Amount = amount.FromSum(),
                OwnerAddress = ByteString.CopyFrom(fromAddress),
                ToAddress = ByteString.CopyFrom(toAddress)
            };
            //创建交易
            Transaction transaction = await _wallet.CreateTransactionAsync(transfer);
            //签名
            SignTransaction(transaction, privateKey);
            //广播
            var result = await _wallet.BroadcastTransactionAsync(transaction);
            if (!result.Result) throw new ChainException($"广播失败：{result.Message.ToStringUtf8()}", address: owner_address);
            return transaction.GetTxID();
        }


        public async Task<string> TransferAsync(string privateKey, string to_address, string contract_address, decimal amount)
        {
            ABIEncode abiEncode = new();
            string encode = "0xa9059cbb" + abiEncode.GetABIParamsEncoded(new TransferFunction() { To = to_address.ToHexAddress(false), Value = amount.FromSum() }).ToHex();
            string owner_address = privateKey.ToAddress();
            AccountInfo account = await this.GetAccountAsync(owner_address, contract_address);
            if (account.Balance < amount) throw new ChainException($"余额不足：{account}", address: owner_address);
            byte[] fromAddress = Base58Encoder.DecodeFromBase58Check(owner_address);
            byte[] contractAddress = Base58Encoder.DecodeFromBase58Check(contract_address);
            TriggerSmartContract contract = new()
            {
                ContractAddress = ByteString.CopyFrom(contractAddress),
                OwnerAddress = ByteString.CopyFrom(fromAddress),
                Data = ByteString.CopyFrom(encode.HexToByteArray()),
            };
            TransactionExtention transactionExtention = await _wallet.TriggerConstantContractAsync(contract);

            Transaction transaction = transactionExtention.Transaction;

            SignTransaction(transaction, privateKey);

            var result = await _wallet.BroadcastTransactionAsync(transaction);
            if (!result.Result) throw new ChainException($"广播失败：{result.Message.ToStringUtf8()}", address: owner_address);
            return transaction.GetTxID();
        }

        /// <summary>
        /// 获取交易信息
        /// </summary>
        /// <param name="txId"></param>
        /// <returns></returns>
        public Task<TransactionInfo> GetTransactionInfoAsync(string txId)
        {
            RestClient client = CreateClient("/wallet/gettransactioninfobyid", Method.Post, out RestRequest request);
            request.AddHeader("content-type", "application/json");
            request.AddParameter("application/json", JsonConvert.SerializeObject(new { value = txId }), ParameterType.RequestBody);
            RestResponse response = client.Execute(request);
            if (!response.IsSuccessful || string.IsNullOrWhiteSpace(response.Content)) throw new ChainException(response.Content, address: string.Empty, hash: txId);
            JObject obj = JObject.Parse(response.Content);
            if (obj.Count == 0) throw new ChainException("交易不存在", address: string.Empty, hash: txId);
            long fee = obj.Value<long>("fee");
            int blockNumber = obj.Value<int>("blockNumber");
            long timestamp = obj.Value<long>("blockTimeStamp");
            string contract_address = obj.Value<string>("contract_address") ?? string.Empty;
            var log = obj.Value<JArray>("log")?.Select(c => new
            {
                address = c.Value<string>("address"),
                topics = c.Value<JArray>("topics")?.Select(t => t.Value<string>()).ToArray(),
                data = c.Value<string>("data")
            }).ToArray();
            if (log == null || log.Length == 0) throw new ChainException("交易未完成", address: string.Empty, hash: txId);
            var topics = log[0].topics;
            if (topics == null) throw new ChainException("topices 不存在");
            string result = obj.Value<JToken>("receipt").Value<string>("result");
            string data = topics[1] + log[0].data;
            TransferFunction transaction = new TransferFunction().DecodeInput(data);
            decimal value = transaction.Value.ToNumber();
            string from = topics[1][24..].ToBase58Address();
            string to = topics[2][24..].ToBase58Address();
            return Task.FromResult(new TransactionInfo()
            {
                BlockNumber = blockNumber,
                ContractAddress = contract_address,
                Value = value,
                From = from,
                To = to,
                Gas = fee.ToNumber(),
                Timestamp = timestamp,
                TransactionHash = txId,
            });
        }

        /// <summary>
        /// 获取最新区块内容
        /// </summary>
        /// <returns></returns>
        public long GetBlockNumber()
        {
            Block block = _wallet.GetNowBlock(new EmptyMessage());
            return block.BlockHeader.RawData.Number;
        }
        /// <summary>
        /// 合约事件监听
        /// </summary>
        /// <param name="@event"></param>
        public void ContractEvent(string contract_address, string eventname, Action<TransactionEvent> @event)
        {
            long blockNumber = GetBlockNumber();
            while (blockNumber != 0)
            {
                Block block = _wallet.GetBlockByNum(new NumberMessage { Num = blockNumber });
                if (block.Transactions.Count == 0)
                {
                    Thread.Sleep(100);
                    continue;
                }
                foreach (Transaction item in block.Transactions)
                {
                    string txId = item.GetTxID();
                    if (item.RawData.Contract.Count > 0)
                    {
                        foreach (var contract in item.RawData.Contract)
                        {
                            string name = contract.Type.ToString();
                            if (name == "TriggerSmartContract")
                            {
                                var parameter = contract.Parameter.Value.ToByteArray().ToHex().GetContractParameter();
                                if (parameter != null && parameter.ContractAddress == contract_address && parameter.MethodID == Web3Utils.GetMethodID(eventname))
                                {
                                    @event(new TransactionEvent { TransactionHash = txId, Block = blockNumber, From = parameter.From, To = parameter.To, Value = parameter.Value });
                                }
                            }
                        }
                    }
                }
                blockNumber++;
                Thread.Sleep(100);
            }
        }

        public decimal GetBalance(string address)
        {
            throw new NotImplementedException();
        }

        public decimal GetBalance(string address, string contractAddress)
        {
            throw new NotImplementedException();
        }

        public AccountInfo GenerateAddress()
        {
            EthECKey ecKey = EthECKey.GenerateKey();
            Nethereum.Web3.Accounts.Account account = new(ecKey.GetPrivateKeyAsBytes());
            return new AccountInfo() { Address = account.Address.ToBase58Address(), PrviateKey = account.PrivateKey };
        }

        public void ContractEvent(string abi, string contract_address, string eventname, Action<string> @event)
        {
            throw new NotImplementedException();
        }

        public Task ContractEventAsync(string abi, string contract_address, string eventname, Action<TransactionEvent> @event)
        {
            throw new NotImplementedException();
        }
    }
}