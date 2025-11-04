# bge_m3_service.py
# Python 服务：专门处理 bge-m3 embedding

from sentence_transformers import SentenceTransformer
from flask import Flask, request, jsonify
import numpy as np

app = Flask(__name__)

# 加载 bge-m3 模型
print("Loading bge-m3 model...")
model = SentenceTransformer('BAAI/bge-m3')
print("Model loaded!")

@app.route('/embed', methods=['POST'])
def embed():
    data = request.json
    text = data.get('text', '')
    
    # 使用 sentence-transformers 生成 embedding
    embedding = model.encode(text, normalize_embeddings=True)
    
    return jsonify({
        'embedding': embedding.tolist()
    })

@app.route('/embed_batch', methods=['POST'])
def embed_batch():
    data = request.json
    texts = data.get('texts', [])
    
    # 批量处理
    embeddings = model.encode(texts, normalize_embeddings=True)
    
    return jsonify({
        'embeddings': embeddings.tolist()
    })

@app.route('/health', methods=['GET'])
def health():
    return jsonify({'status': 'ok', 'model': 'bge-m3'})

if __name__ == '__main__':
    app.run(host='127.0.0.1', port=8081)
