using System.Text.Json.Serialization;
using FluentAssertions;
using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;

// def __init__(
//     self,
//     vocab_size=51200,
//     hidden_size=2048,
//     intermediate_size=8192,
//     num_hidden_layers=24,
//     num_attention_heads=32,
//     num_key_value_heads=None,
//     resid_pdrop=0.0,
//     embd_pdrop=0.0,
//     attention_dropout=0.0,
//     hidden_act="gelu_new",
//     max_position_embeddings=2048,
//     initializer_range=0.02,
//     layer_norm_eps=1e-5,
//     use_cache=True,
//     tie_word_embeddings=False,
//     rope_theta=10000.0,
//     rope_scaling=None,
//     partial_rotary_factor=0.5,
//     qk_layernorm=False,
//     bos_token_id=1,
//     eos_token_id=2,
//     **kwargs,
// ):
public class PhiConfig
{
    [JsonPropertyName("vocab_size")]
    public int VocabSize { get; set; } = 51200;

    [JsonPropertyName("hidden_size")]
    public int HiddenSize { get; set; } = 2048;

    [JsonPropertyName("intermediate_size")]
    public int IntermediateSize { get; set; } = 8192;

    [JsonPropertyName("num_hidden_layers")]
    public int NumHiddenLayers { get; set; } = 24;

    [JsonPropertyName("num_attention_heads")]
    public int NumAttentionHeads { get; set; } = 32;

    [JsonPropertyName("num_key_value_heads")]
    public int? NumKeyValueHeads { get; set; } = null;

    [JsonPropertyName("resid_pdrop")]
    public double ResidPdrop { get; set; } = 0.0;

    [JsonPropertyName("embd_pdrop")]
    public double EmbdPdrop { get; set; } = 0.0;

    [JsonPropertyName("attention_dropout")]
    public double AttentionDropout { get; set; } = 0.0;

    [JsonPropertyName("hidden_act")]
    public string HiddenAct { get; set; } = "gelu_new";

    [JsonPropertyName("max_position_embeddings")]
    public int MaxPositionEmbeddings { get; set; } = 2048;

    [JsonPropertyName("initializer_range")]
    public double InitializerRange { get; set; } = 0.02;

    [JsonPropertyName("layer_norm_eps")]
    public double LayerNormEps { get; set; } = 1e-5;

    [JsonPropertyName("use_cache")]
    public bool UseCache { get; set; } = true;

    [JsonPropertyName("tie_word_embeddings")]
    public bool TieWordEmbeddings { get; set; } = false;

    [JsonPropertyName("rope_theta")]
    public double RopeTheta { get; set; } = 10000.0;

    // [JsonPropertyName("rope_scaling")]
    // public double? RopeScaling { get; set; } = null;

    [JsonPropertyName("partial_rotary_factor")]
    public double PartialRotaryFactor { get; set; } = 0.5;

    [JsonPropertyName("qk_layernorm")]
    public bool QkLayernorm { get; set; } = false;

    [JsonPropertyName("bos_token_id")]
    public int BosTokenId { get; set; } = 1;

    [JsonPropertyName("eos_token_id")]
    public int EosTokenId { get; set; } = 2;

    public ScalarType Dtype => ScalarType.BFloat16;
}

public class NewGELUActivation : torch.nn.Module<Tensor, Tensor>
{
    public NewGELUActivation()
        : base(nameof(NewGELUActivation))
    {
    }

    public override Tensor forward(Tensor input)
    {
        // return 0.5 * input * (1.0 + torch.tanh(math.sqrt(2.0 / math.pi) * (input + 0.044715 * torch.pow(input, 3.0))))

        return 0.5 * input * (1.0 + torch.tanh(torch.sqrt(2.0 / Math.PI) * (input + 0.044715 * torch.pow(input, 3.0))));
    }
}

public class PhiMLP : torch.nn.Module<Tensor, Tensor>
{
    private readonly Linear fc1;
    private readonly Linear fc2;
    private readonly torch.nn.Module<Tensor, Tensor> activation_fn;

    public PhiMLP(PhiConfig config)
        : base(nameof(PhiMLP))
    {
        this.fc1 = torch.nn.Linear(config.HiddenSize, config.IntermediateSize, dtype: config.Dtype);
        this.fc2 = torch.nn.Linear(config.IntermediateSize, config.HiddenSize, dtype: config.Dtype);
        this.activation_fn = new NewGELUActivation();
    }
    public override Tensor forward(Tensor input)
    {
        input = this.fc1.forward(input);
        input = this.activation_fn.forward(input);
        input = this.fc2.forward(input);

        return input;
    }
}

public class PhiRotaryEmbedding : nn.Module<
    Tensor, // input
    int, // seq_len
    (
        Tensor, // cos
        Tensor // sin
    )>
{
    private readonly double _base;
    private readonly int _maxPositionEmbeddings;
    private readonly int _dim;

    public PhiRotaryEmbedding(double baseValue, int maxPositionEmbeddings, int dim)
        : base(nameof(PhiRotaryEmbedding))
    {
        _base = baseValue;
        _maxPositionEmbeddings = maxPositionEmbeddings;
        _dim = dim;
        Console.WriteLine($"base: {_base} maxPositionEmbeddings: {_maxPositionEmbeddings} dim: {_dim}");
        var thetaNumerator = torch.arange(0, _dim, 2).to(torch.float32);
        this.register_buffer("inv_freq", torch.pow(baseValue, -1.0f * (thetaNumerator / dim)), persistent: false);
    }

    public int Dim => _dim;

    public override (Tensor, Tensor) forward(Tensor x, int seqLen)
    {
        // TODO
        // can be calculated once and cached
        var inv_freq = this.get_buffer("inv_freq").to(x.device);
        var t = torch.arange(seqLen, dtype: inv_freq.dtype, device: inv_freq.device);
        var freqs = torch.outer(t, inv_freq).to(torch.float32);
        var emb = torch.cat([freqs, freqs], dim: -1);

        var cos = torch.cos(emb);
        var sin = torch.sin(emb);

        return (cos[..seqLen].to_type(x.dtype), sin[..seqLen].to_type(x.dtype));
    }
}

public class PhiAttention : nn.Module<
    Tensor, // hidden_states
    Tensor, // position_ids
    Tensor?, // attention_mask
    Tensor?, // past_key_value
    bool, // output_attentions
    (
        Tensor, // hidden_states,
        Tensor?, // attentions,
        Tensor? // present_key_value
    )>
{
    private readonly int? layerIdx;
    private PhiConfig config;
    private readonly double attentionDropout;
    private readonly int hiddenSize;
    private readonly int numAttentionHeads;
    private readonly int headDim;
    private readonly int numKeyValueHeads;
    private readonly int numKeyValueGroups;
    private readonly int maxPositionEmbeddings;
    private readonly double ropeTheta;
    private readonly double partialRotaryFactor;
    private readonly bool isCausal;
    private readonly Linear q_proj;
    private readonly Linear k_proj;
    private readonly Linear v_proj;
    private readonly Linear dense;
    private readonly bool qk_layernorm;
    private readonly LayerNorm? q_layernorm;
    private readonly LayerNorm? k_layernorm;

    private readonly PhiRotaryEmbedding phiRotaryEmbedding;

    // cache_k, cache_v
    private Tensor? cache_k;
    private Tensor? cache_v;

    public PhiAttention(PhiConfig config, int? layerIdx = null)
        : base(nameof(PhiAttention))
    {
        this.layerIdx = layerIdx;
        this.config = config;
        
        this.attentionDropout = config.AttentionDropout;
        this.hiddenSize = config.HiddenSize;
        this.numAttentionHeads = config.NumAttentionHeads;
        this.headDim = this.hiddenSize / this.numAttentionHeads;
        this.numKeyValueHeads = config.NumKeyValueHeads ?? throw new ArgumentException("num_key_value_heads must be specified");
        this.numKeyValueGroups = this.numAttentionHeads / this.numKeyValueHeads;
        this.maxPositionEmbeddings = config.MaxPositionEmbeddings;
        this.ropeTheta = config.RopeTheta;
        this.partialRotaryFactor = config.PartialRotaryFactor;
        this.isCausal = true;

        (this.headDim * this.numAttentionHeads).Should().Be(this.hiddenSize, "hidden_size must be divisible by num_attention_heads");
        this.q_proj = nn.Linear(this.hiddenSize, this.numAttentionHeads * this.headDim, hasBias: true, dtype: config.Dtype);
        this.k_proj = nn.Linear(this.hiddenSize, this.numKeyValueHeads * this.headDim, hasBias: true, dtype: config.Dtype);
        this.v_proj = nn.Linear(this.hiddenSize, this.numKeyValueHeads * this.headDim, hasBias: true, dtype: config.Dtype);
        this.dense = nn.Linear(this.numAttentionHeads * this.headDim, this.hiddenSize, hasBias: true, dtype: config.Dtype);

        this.qk_layernorm = config.QkLayernorm;
        if (this.qk_layernorm)
        {
            this.q_layernorm = nn.LayerNorm(this.hiddenSize / this.numAttentionHeads, eps: config.LayerNormEps, elementwise_affine: true, dtype: config.Dtype);
            this.k_layernorm = nn.LayerNorm(this.hiddenSize / this.numAttentionHeads, eps: config.LayerNormEps, elementwise_affine: true, dtype: config.Dtype);
        }

        this.RegisterComponents();
        this.phiRotaryEmbedding = new PhiRotaryEmbedding(
            dim: (int)(this.partialRotaryFactor * this.headDim),
            maxPositionEmbeddings: this.maxPositionEmbeddings,
            baseValue: this.config.RopeTheta);
    }
    public override (Tensor, Tensor, Tensor?) forward(
        Tensor hiddenStates,
        Tensor positionIds,
        Tensor? attentionMask = null,
        Tensor? pastKeyValue = null,
        bool outputAttentions = false)
    {
        var batchSize = hiddenStates.shape[0];
        var seqLen = (int)hiddenStates.shape[1];

        var queryStates = this.q_proj.forward(hiddenStates);
        var keyStates = this.k_proj.forward(hiddenStates);
        var valueStates = this.v_proj.forward(hiddenStates);

        if (this.qk_layernorm)
        {
            queryStates = this.q_layernorm!.forward(queryStates);
            keyStates = this.k_layernorm!.forward(keyStates);
        }

        queryStates = queryStates.view(batchSize, seqLen, this.numAttentionHeads, this.headDim).transpose(1, 2);
        keyStates = keyStates.view(batchSize, seqLen, this.numKeyValueHeads, this.headDim).transpose(1, 2);
        valueStates = valueStates.view(batchSize, seqLen, this.numKeyValueHeads, this.headDim).transpose(1, 2);

        var kvSeqLen = this.cache_k is null ? (int)keyStates.shape[2] : (int)this.cache_k.shape[2];
        //TODO
        // implement cache logic
        // if past_key_value is not None:

        (var cos, var sin) = this.phiRotaryEmbedding.forward(valueStates, kvSeqLen);
        // split the last dim of queryStates and keyStates into rotary and non-rotary parts
        // shape: [batch_size, num_heads, seq_len, head_dim]
        // queryRot: [batch_size, num_heads, seq_len, :head_dim * partial_rotary_factor]
        // queryPass: [batch_size, num_heads, seq_len, head_dim * partial_rotary_factor:]
        var queryRot = queryStates[..,..,.., ..this.phiRotaryEmbedding.Dim];
        var queryPass = queryStates[..,..,.., this.phiRotaryEmbedding.Dim..];
        var keyRot = keyStates[.., .., .., ..this.phiRotaryEmbedding.Dim];
        var keyPass = keyStates[.., .., .., this.phiRotaryEmbedding.Dim..];

        (var qRot, var kRot) = Utils.ApplyRotaryPosEmb(queryRot, keyRot, cos, sin, positionIds);
        qRot.Peek("qRot", 10);
        queryStates = torch.cat([qRot, queryPass], dim: -1);
        keyStates = torch.cat([kRot, keyPass], dim: -1);
        if (this.cache_k is null || this.cache_v is null)
        {
            this.cache_k = keyStates;
            this.cache_v = valueStates;
        }
        else
        {
            this.cache_k = torch.cat([this.cache_k, keyStates], dim: -2);
            this.cache_v = torch.cat([this.cache_v, valueStates], dim: -2);
        }

        keyStates = this.cache_k;
        valueStates = this.cache_v;

        keyStates = Utils.RepeatKV(keyStates, this.numKeyValueGroups);
        valueStates = Utils.RepeatKV(valueStates, this.numKeyValueGroups);

        // Queries and keys upcast to fp32 is required by Phi-2 to avoid overflow
        var attnWeights = torch.matmul(queryStates.to(torch.float32), keyStates.to(torch.float32).transpose(2, 3));
        attnWeights = attnWeights / Math.Sqrt(this.headDim);

        attnWeights = nn.functional.softmax(attnWeights, dim: -1, dtype: ScalarType.Float32).to_type(hiddenStates.dtype);
        attnWeights = nn.functional.dropout(attnWeights, this.attentionDropout, training: this.training);

        var attnOutput = torch.matmul(attnWeights, valueStates);
        attnOutput = attnOutput.transpose(1, 2).contiguous();
        attnOutput = attnOutput.reshape(batchSize, seqLen, this.hiddenSize);

        attnOutput = this.dense.forward(attnOutput);

        return (attnOutput, attnWeights, pastKeyValue);
    }
}

public class PhiDecoderLayer : nn.Module<
    Tensor, // hidden_states
    Tensor, // position_ids
    Tensor?, // attention_mask
    Tensor?, // past_key_value
    bool, // use_cache
    bool, // output_attentions
    (
        Tensor, // hidden_states,
        Tensor?, // attentions,
        Tensor? // present_key_value
    )>
{
    private readonly int? layerIdx;
    private PhiAttention self_attn;
    private PhiMLP mlp;
    private LayerNorm input_layernorm;
    private Dropout resid_dropout;

    public PhiDecoderLayer(PhiConfig config, int? layerIdx = null)
        : base(nameof(PhiDecoderLayer))
    {
        this.layerIdx = layerIdx;
        this.self_attn = new PhiAttention(config, layerIdx);
        this.mlp = new PhiMLP(config);
        this.input_layernorm = nn.LayerNorm(config.HiddenSize, eps: config.LayerNormEps, dtype: config.Dtype);
        this.resid_dropout = nn.Dropout(config.ResidPdrop);
    }

    public override (Tensor, Tensor, Tensor?) forward(
        Tensor hiddenStates,
        Tensor positionIds,
        Tensor? attentionMask = null,
        Tensor? pastKeyValue = null,
        bool useCache = false,
        bool outputAttentions = false)
    {
        var residual = hiddenStates;
        hiddenStates = this.input_layernorm.forward(hiddenStates);
        (var attnOutput, var attnWeights, var presentKeyValue) = this.self_attn.forward(
            hiddenStates: hiddenStates,
            positionIds: positionIds,
            attentionMask: attentionMask,
            pastKeyValue: pastKeyValue,
            outputAttentions: outputAttentions);

        var attn_outputs = this.resid_dropout.forward(attnOutput);
        var feed_forward_hiddenStates = this.resid_dropout.forward(this.mlp.forward(attn_outputs));

        hiddenStates = residual + feed_forward_hiddenStates + attnOutput;

        return (hiddenStates, attnWeights, presentKeyValue);
    }
}

public class PhiModel : nn.Module<
    Tensor, // input_ids
    Tensor?, // attention_mask
    Tensor?, // past_key_value
    Tensor?, // position_ids
    Tensor?, //input embeddings
    (
        bool, // use_cache
        bool, // output_attentions
        bool // output_hidden_states
    ),
    (
        Tensor, // hidden_states,
        Tensor?, // attentions,
        Tensor? // present_key_value
    )>
{
    private readonly PhiConfig config;
    private readonly Embedding embed_tokens;
    private readonly Dropout embed_dropout;
    private readonly LayerNorm final_layernorm;
    private readonly ModuleList<PhiDecoderLayer> layers;

    public PhiModel(PhiConfig config)
        : base(nameof(PhiModel))
    {
        this.config = config;
        this.embed_tokens = nn.Embedding(config.VocabSize, config.HiddenSize, dtype: config.Dtype);
        this.embed_dropout = nn.Dropout(config.EmbdPdrop);
        this.final_layernorm = nn.LayerNorm(config.HiddenSize, eps: config.LayerNormEps, dtype: config.Dtype);
        this.layers = new ModuleList<PhiDecoderLayer>(Enumerable.Range(0, config.NumHiddenLayers).Select(_ => new PhiDecoderLayer(config)).ToArray());

        this.RegisterComponents();
    }

    public PhiConfig Config => this.config;

    public override (Tensor, Tensor?, Tensor?) forward(
        Tensor inputIds,
        Tensor? attentionMask = null,
        Tensor? pastKeyValue = null,
        Tensor? positionIds = null,
        Tensor? inputEmbeddings = null,
        (bool, bool, bool) options = default) // use_cache, output_attentions, output_hidden_states
    {
        (var outputAttentions, var outputHiddenStates, var useCache) = options;

        // TODO
        // add support for inputEmbeddings
        if (inputEmbeddings is not null)
        {
            throw new NotImplementedException("inputEmbeddings is not supported");
        }
        
        inputEmbeddings = this.embed_tokens.forward(inputIds);
        inputEmbeddings = this.embed_dropout.forward(inputEmbeddings);
        var batchSize = inputIds.shape[0];
        var seqLen = (int)inputIds.shape[1];

        // TODO
        // add support for cache


        var pastKeyValueLength = 0;
        if (positionIds is null)
        {
            positionIds = torch.arange(pastKeyValueLength, seqLen + pastKeyValueLength, dtype: inputIds.dtype, device: inputIds.device);
            positionIds = positionIds.unsqueeze(0);
        }

        // attention
        // use 4d attention mask
        if (attentionMask is not null)
        {
            attentionMask = this.Prepare4DCasualAttentionMask(attentionMask, seqLen, inputIds.dtype);
        }

        var hiddenStates = inputEmbeddings;
        hiddenStates.Peek("hiddenStates", 10);

        for (int i = 0; i < this.layers.Count; i++)
        {
            (hiddenStates, _, pastKeyValue) = this.layers[i].forward(
                hiddenStates: hiddenStates,
                positionIds: positionIds,
                attentionMask: attentionMask,
                pastKeyValue: pastKeyValue,
                useCache: useCache,
                outputAttentions: outputAttentions);
        }

        hiddenStates = this.final_layernorm.forward(hiddenStates);

        return (hiddenStates, null, null);
    }

    private Tensor Prepare4DCasualAttentionMask(
        Tensor attentionMask,
        int targetLength,
        ScalarType dtype)
    {
        //  Creates a causal 4D mask of shape `(batch_size, 1, targetLength, seqLengh)` from a 2D mask of shape `(batch_size, seqLengh)`.
        //  This is the causal mask that is used for casual attention.

        var batchSize = attentionMask.shape[0];
        var seqLen = attentionMask.shape[1];
        var expandedMask = attentionMask.unsqueeze(1).unsqueeze(2);

        // expandedMask = expandedMask.expand(batchSize, 1, targetLength, seqLen);
        expandedMask = expandedMask.expand(new long[] { batchSize, 1, targetLength, seqLen });

        var invertedMask = expandedMask.logical_not();

        return invertedMask.masked_fill_(expandedMask.to_type(ScalarType.Bool), float.NegativeInfinity).to_type(dtype);
    }
}

public class PhiModelInferenceWrapper : nn.Module<
    Tensor, // input_ids
    Tensor?, // attention_mask
    Tensor?, // past_key_value
    Tensor?, // position_ids
    Tensor?, //input embeddings
    (
        bool, // use_cache
        bool, // output_attentions
        bool // output_hidden_states
    ),
    (
        Tensor, // hidden_states,
        Tensor?, // attentions,
        Tensor? // present_key_value
    )>
{
    private readonly PhiModel model;

    private readonly Linear lm_head;

    public PhiModelInferenceWrapper(PhiModel model)
        : base(nameof(PhiModelInferenceWrapper))
    {
        this.model = model;
        this.lm_head = nn.Linear(model.Config.HiddenSize, model.Config.VocabSize, dtype: model.Config.Dtype);
        this.RegisterComponents();
    }

    public override (Tensor, Tensor?, Tensor?) forward(
        Tensor inputIds,
        Tensor? attentionMask = null,
        Tensor? pastKeyValue = null,
        Tensor? positionIds = null,
        Tensor? inputEmbeddings = null,
        (bool, bool, bool) options = default) // use_cache, output_attentions, output_hidden_states
    {
        var output = this.model.forward(inputIds, attentionMask, pastKeyValue, positionIds, inputEmbeddings, options);
        var hidden_state = output.Item1;

        var lm_logits = this.lm_head.forward(hidden_state);

        return (lm_logits, output.Item2, output.Item3);
    }
}
  